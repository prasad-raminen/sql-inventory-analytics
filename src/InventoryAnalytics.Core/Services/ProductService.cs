using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;

namespace InventoryAnalytics.Core.Services;

public interface IProductService
{
    Task<IReadOnlyList<Product>> GetProductsAsync(int? categoryId, bool includeInactive, CancellationToken cancellationToken = default);

    Task<Product> GetProductAsync(int productId, CancellationToken cancellationToken = default);

    Task<ProductAvailability> GetAvailabilityAsync(int productId, CancellationToken cancellationToken = default);
}

public sealed class ProductService(IProductRepository products) : IProductService
{
    public Task<IReadOnlyList<Product>> GetProductsAsync(int? categoryId, bool includeInactive, CancellationToken cancellationToken = default)
    {
        if (categoryId is <= 0)
        {
            throw new RequestValidationException("categoryId", "categoryId must be a positive number.");
        }

        return products.GetProductsAsync(categoryId, includeInactive, cancellationToken);
    }

    public async Task<Product> GetProductAsync(int productId, CancellationToken cancellationToken = default)
    {
        return await products.GetByIdAsync(productId, cancellationToken)
            ?? throw NotFoundException.For("Product", productId);
    }

    public async Task<ProductAvailability> GetAvailabilityAsync(int productId, CancellationToken cancellationToken = default)
    {
        Product product = await GetProductAsync(productId, cancellationToken);
        IReadOnlyList<WarehouseStock> stock = await products.GetStockByWarehouseAsync(productId, cancellationToken);

        return new ProductAvailability(
            product.ProductId,
            product.Sku,
            product.Name,
            product.ReorderLevel,
            stock.Sum(s => s.QuantityOnHand),
            stock);
    }
}
