using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryAnalytics.UnitTests.Services;

public sealed class OrderServiceTests
{
    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly OrderService _sut;

    public OrderServiceTests()
    {
        _sut = new OrderService(_repository, NullLogger<OrderService>.Instance);
    }

    private static PlaceOrderCommand ValidCommand() =>
        new(CustomerId: 3, WarehouseId: 1, Lines: [new OrderLine(10, 2), new OrderLine(11, 1)]);

    [Fact]
    public async Task PlaceOrderAsync_ValidCommand_ReturnsOrderFromRepository()
    {
        PlaceOrderCommand command = ValidCommand();
        var expected = new OrderSummary { OrderId = 42, CustomerId = 3, WarehouseId = 1, Status = "Placed", TotalAmount = 250m };
        _repository.PlaceOrderAsync(command, Arg.Any<CancellationToken>()).Returns(expected);

        OrderSummary result = await _sut.PlaceOrderAsync(command);

        result.Should().Be(expected);
        await _repository.Received(1).PlaceOrderAsync(command, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrderAsync_InsufficientStock_PropagatesConflict()
    {
        _repository.PlaceOrderAsync(Arg.Any<PlaceOrderCommand>(), Arg.Any<CancellationToken>())
            .Returns<OrderSummary>(_ => throw new InsufficientStockException("Insufficient stock for product 10."));

        Func<Task> act = () => _sut.PlaceOrderAsync(ValidCommand());

        await act.Should().ThrowAsync<InsufficientStockException>().WithMessage("*product 10*");
    }

    [Fact]
    public async Task PlaceOrderAsync_EmptyLines_ThrowsValidationWithoutCallingDatabase()
    {
        var command = new PlaceOrderCommand(3, 1, []);

        Func<Task> act = () => _sut.PlaceOrderAsync(command);

        (await act.Should().ThrowAsync<RequestValidationException>())
            .Which.Errors.Should().ContainKey("lines");
        await _repository.DidNotReceiveWithAnyArgs().PlaceOrderAsync(default!, default);
    }

    [Fact]
    public async Task PlaceOrderAsync_InvalidIdsAndQuantities_ReportsEveryFieldError()
    {
        var command = new PlaceOrderCommand(
            CustomerId: 0,
            WarehouseId: -1,
            Lines: [new OrderLine(0, 1), new OrderLine(5, 0), new OrderLine(6, OrderService.MaxQuantityPerLine + 1)]);

        Func<Task> act = () => _sut.PlaceOrderAsync(command);

        (await act.Should().ThrowAsync<RequestValidationException>())
            .Which.Errors.Keys.Should().BeEquivalentTo(
                "customerId", "warehouseId", "lines[0].productId", "lines[1].quantity", "lines[2].quantity");
    }

    [Fact]
    public async Task PlaceOrderAsync_TooManyLines_ThrowsValidation()
    {
        List<OrderLine> lines = Enumerable.Range(1, OrderService.MaxLines + 1).Select(id => new OrderLine(id, 1)).ToList();

        Func<Task> act = () => _sut.PlaceOrderAsync(new PlaceOrderCommand(1, 1, lines));

        (await act.Should().ThrowAsync<RequestValidationException>())
            .Which.Errors["lines"].Should().ContainSingle().Which.Should().Contain(OrderService.MaxLines.ToString());
    }

    [Fact]
    public async Task GetOrderAsync_UnknownOrder_ThrowsNotFound()
    {
        _repository.GetOrderAsync(99, Arg.Any<CancellationToken>()).Returns((OrderDetails?)null);

        Func<Task> act = () => _sut.GetOrderAsync(99);

        await act.Should().ThrowAsync<NotFoundException>().WithMessage("Order 99 was not found.");
    }

    [Fact]
    public async Task GetOrderAsync_ExistingOrder_ReturnsDetails()
    {
        var details = new OrderDetails(new OrderHeader { OrderId = 7 }, [new OrderDetailsLine { ProductId = 1, Quantity = 2 }]);
        _repository.GetOrderAsync(7, Arg.Any<CancellationToken>()).Returns(details);

        OrderDetails result = await _sut.GetOrderAsync(7);

        result.Should().BeSameAs(details);
    }
}
