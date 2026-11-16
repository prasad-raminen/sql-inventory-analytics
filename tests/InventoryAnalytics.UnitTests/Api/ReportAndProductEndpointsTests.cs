using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.UnitTests.TestSupport;

namespace InventoryAnalytics.UnitTests.Api;

public sealed class ReportAndProductEndpointsTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReportAndProductEndpointsTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task TopProducts_BindsQueryAndReturnsResolvedPeriod()
    {
        _factory.Reports.GetTopProductsAsync(Arg.Any<ReportPeriod>(), 3, null, Arg.Any<CancellationToken>())
            .Returns([new TopProduct { ProductId = 4, Sku = "MM-VHC-02", Revenue = 1000m, RevenueRank = 1 }]);

        JsonElement body = await _client.GetFromJsonAsync<JsonElement>(
            "/api/reports/top-products?from=2025-01-01&to=2025-12-31&top=3");

        body.GetProperty("period").GetProperty("fromUtc").GetDateTime().Should().Be(new DateTime(2025, 1, 1));
        body.GetProperty("period").GetProperty("toUtcExclusive").GetDateTime().Should().Be(new DateTime(2026, 1, 1));
        JsonElement item = body.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("sku").GetString().Should().Be("MM-VHC-02");
        item.GetProperty("revenueRank").GetInt64().Should().Be(1);
    }

    [Theory]
    [InlineData("/api/reports/top-products?top=0")]
    [InlineData("/api/reports/top-products?top=abc")]
    [InlineData("/api/reports/top-products?from=2025-02-01&to=2025-01-01")]
    [InlineData("/api/reports/sales-by-category?from=not-a-date")]
    [InlineData("/api/reports/monthly-sales?year=1900")]
    [InlineData("/api/reports/low-stock?warehouseId=-1")]
    public async Task Reports_InvalidQuery_Return400(string url)
    {
        HttpResponseMessage response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task LowStock_FiltersByWarehouse()
    {
        _factory.Reports.GetLowStockAsync(2, Arg.Any<CancellationToken>())
            .Returns([new LowStockItem { Sku = "MM-VHC-02", WarehouseId = 2, QuantityOnHand = 0, SuggestedReorderQty = 4 }]);

        JsonElement body = await _client.GetFromJsonAsync<JsonElement>("/api/reports/low-stock?warehouseId=2");

        body.EnumerateArray().Single().GetProperty("suggestedReorderQty").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task SalesByCategory_DefaultsToRollUp()
    {
        await _client.GetAsync("/api/reports/sales-by-category?from=2025-01-01&to=2025-12-31");

        await _factory.Reports.Received(1).GetSalesByCategoryAsync(Arg.Any<ReportPeriod>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProductStock_ReturnsWarehouseBreakdown()
    {
        _factory.Products.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Product { ProductId = 1, Sku = "LS-PRO-360", Name = "Scanner", ReorderLevel = 3 });
        _factory.Products.GetStockByWarehouseAsync(1, Arg.Any<CancellationToken>())
            .Returns([new WarehouseStock { WarehouseCode = "AMS01", QuantityOnHand = 1, IsLowStock = true },
                      new WarehouseStock { WarehouseCode = "CHI01", QuantityOnHand = 9 }]);

        JsonElement body = await _client.GetFromJsonAsync<JsonElement>("/api/products/1/stock");

        body.GetProperty("totalOnHand").GetInt32().Should().Be(10);
        body.GetProperty("warehouses").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Product_Unknown_Returns404()
    {
        _factory.Products.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns((Product?)null);

        HttpResponseMessage response = await _client.GetAsync("/api/products/77");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LivenessProbe_DoesNotTouchDatabase()
    {
        HttpResponseMessage response = await _client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
