using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.UnitTests.TestSupport;

namespace InventoryAnalytics.UnitTests.Api;

public sealed class OrderEndpointsTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public OrderEndpointsTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static object ValidBody() => new
    {
        customerId = 1,
        warehouseId = 2,
        lines = new[] { new { productId = 10, quantity = 3 } },
    };

    [Fact]
    public async Task PostOrder_Success_Returns201WithLocation()
    {
        _factory.Orders.PlaceOrderAsync(Arg.Any<PlaceOrderCommand>(), Arg.Any<CancellationToken>())
            .Returns(new OrderSummary { OrderId = 42, CustomerId = 1, WarehouseId = 2, Status = "Placed", TotalAmount = 300m });

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/orders", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().EndWith("/api/orders/42");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("orderId").GetInt32().Should().Be(42);
        body.GetProperty("totalAmount").GetDecimal().Should().Be(300m);

        await _factory.Orders.Received(1).PlaceOrderAsync(
            Arg.Is<PlaceOrderCommand>(c => c.CustomerId == 1 && c.WarehouseId == 2
                && c.Lines.Count == 1 && c.Lines[0] == new OrderLine(10, 3)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostOrder_InsufficientStock_Returns409ProblemDetails()
    {
        _factory.Orders.PlaceOrderAsync(Arg.Any<PlaceOrderCommand>(), Arg.Any<CancellationToken>())
            .Returns<OrderSummary>(_ => throw new InsufficientStockException(
                "Insufficient stock for product 10 in warehouse 2: requested 3, available 1."));

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/orders", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("title").GetString().Should().Be("Insufficient stock");
        problem.GetProperty("detail").GetString().Should().Contain("available 1");
        problem.GetProperty("errorCode").GetString().Should().Be("insufficient_stock");
    }

    [Fact]
    public async Task PostOrder_UnknownProduct_Returns404()
    {
        _factory.Orders.PlaceOrderAsync(Arg.Any<PlaceOrderCommand>(), Arg.Any<CancellationToken>())
            .Returns<OrderSummary>(_ => throw new NotFoundException("Product 10 was not found or is inactive."));

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/orders", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostOrder_MissingLines_Returns400ValidationProblem()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/orders", new { customerId = 1, warehouseId = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("lines", out _).Should().BeTrue();
        await _factory.Orders.DidNotReceiveWithAnyArgs().PlaceOrderAsync(default!, default);
    }

    [Fact]
    public async Task PostOrder_MalformedJson_Returns400()
    {
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _client.PostAsync("/api/orders", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetOrder_Unknown_Returns404ProblemDetails()
    {
        _factory.Orders.GetOrderAsync(5, Arg.Any<CancellationToken>()).Returns((OrderDetails?)null);

        HttpResponseMessage response = await _client.GetAsync("/api/orders/5");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("detail").GetString().Should().Be("Order 5 was not found.");
    }

    [Fact]
    public async Task UnexpectedRepositoryFailure_Returns500WithoutLeakingDetails()
    {
        _factory.Orders.GetOrderAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<OrderDetails?>(_ => throw new InvalidOperationException("secret connection detail"));

        HttpResponseMessage response = await _client.GetAsync("/api/orders/1");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("secret connection detail");
    }
}
