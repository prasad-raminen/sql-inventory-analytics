using InventoryAnalytics.Core.Models;

namespace InventoryAnalytics.Core.Abstractions;

/// <summary>Read access to the product catalog and per-warehouse stock.</summary>
public interface IProductRepository
{
    Task<IReadOnlyList<Product>> GetProductsAsync(int? categoryId, bool includeInactive, CancellationToken cancellationToken);

    Task<Product?> GetByIdAsync(int productId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WarehouseStock>> GetStockByWarehouseAsync(int productId, CancellationToken cancellationToken);
}

/// <summary>Analytics queries backed by views and stored procedures.</summary>
public interface IReportRepository
{
    Task<IReadOnlyList<LowStockItem>> GetLowStockAsync(int? warehouseId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MonthlySales>> GetMonthlySalesAsync(int? year, CancellationToken cancellationToken);

    Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(ReportPeriod period, int top, int? categoryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CategorySales>> GetSalesByCategoryAsync(ReportPeriod period, bool rollUpToRoot, CancellationToken cancellationToken);
}

/// <summary>Order placement and lookup.</summary>
public interface IOrderRepository
{
    /// <summary>
    /// Places the order atomically. Implementations translate database errors into
    /// <see cref="Errors.InsufficientStockException"/>, <see cref="Errors.NotFoundException"/>
    /// and <see cref="Errors.RequestValidationException"/>.
    /// </summary>
    Task<OrderSummary> PlaceOrderAsync(PlaceOrderCommand command, CancellationToken cancellationToken);

    Task<OrderDetails?> GetOrderAsync(int orderId, CancellationToken cancellationToken);
}
