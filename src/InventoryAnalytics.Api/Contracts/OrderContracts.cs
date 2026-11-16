using InventoryAnalytics.Core.Models;

namespace InventoryAnalytics.Api.Contracts;

/// <summary>Request body for <c>POST /api/orders</c>.</summary>
/// <param name="CustomerId">Existing customer placing the order.</param>
/// <param name="WarehouseId">Warehouse that fulfils (and is decremented for) every line.</param>
/// <param name="Lines">Products and quantities; duplicate products are merged by the database.</param>
public sealed record PlaceOrderRequest(int CustomerId, int WarehouseId, IReadOnlyList<OrderLineRequest>? Lines)
{
    public PlaceOrderCommand ToCommand() =>
        new(CustomerId, WarehouseId, Lines?.Select(l => new OrderLine(l.ProductId, l.Quantity)).ToList() ?? []);
}

/// <summary>One line of <see cref="PlaceOrderRequest"/>.</summary>
public sealed record OrderLineRequest(int ProductId, int Quantity);
