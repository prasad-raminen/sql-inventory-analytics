using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Core.Services;

namespace InventoryAnalytics.UnitTests.Services;

public sealed class ProductServiceTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly ProductService _sut;

    public ProductServiceTests()
    {
        _sut = new ProductService(_repository);
    }

    [Fact]
    public async Task GetProductAsync_UnknownProduct_ThrowsNotFound()
    {
        _repository.GetByIdAsync(404, Arg.Any<CancellationToken>()).Returns((Product?)null);

        Func<Task> act = () => _sut.GetProductAsync(404);

        await act.Should().ThrowAsync<NotFoundException>().WithMessage("Product 404 was not found.");
    }

    [Fact]
    public async Task GetAvailabilityAsync_SumsStockAcrossWarehouses()
    {
        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Product { ProductId = 1, Sku = "LS-PRO-360", Name = "Scanner", ReorderLevel = 3 });
        _repository.GetStockByWarehouseAsync(1, Arg.Any<CancellationToken>())
            .Returns([
                new WarehouseStock { WarehouseId = 1, QuantityOnHand = 1, IsLowStock = true },
                new WarehouseStock { WarehouseId = 2, QuantityOnHand = 40 },
                new WarehouseStock { WarehouseId = 3, QuantityOnHand = 0, IsLowStock = true },
            ]);

        ProductAvailability result = await _sut.GetAvailabilityAsync(1);

        result.TotalOnHand.Should().Be(41);
        result.Sku.Should().Be("LS-PRO-360");
        result.Warehouses.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetAvailabilityAsync_UnknownProduct_DoesNotQueryStock()
    {
        _repository.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns((Product?)null);

        Func<Task> act = () => _sut.GetAvailabilityAsync(9);

        await act.Should().ThrowAsync<NotFoundException>();
        await _repository.DidNotReceiveWithAnyArgs().GetStockByWarehouseAsync(default, default);
    }

    [Fact]
    public async Task GetProductsAsync_PassesFiltersThrough()
    {
        _repository.GetProductsAsync(2, true, Arg.Any<CancellationToken>()).Returns([new Product { ProductId = 5 }]);

        IReadOnlyList<Product> result = await _sut.GetProductsAsync(2, includeInactive: true);

        result.Should().ContainSingle().Which.ProductId.Should().Be(5);
    }

    [Fact]
    public async Task GetProductsAsync_NonPositiveCategory_ThrowsValidation()
    {
        Func<Task> act = () => _sut.GetProductsAsync(0, includeInactive: false);

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Errors.Should().ContainKey("categoryId");
    }
}
