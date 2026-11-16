namespace InventoryAnalytics.Core.Models;

/// <summary>One requested line of a new order.</summary>
public sealed record OrderLine(int ProductId, int Quantity);

/// <summary>Everything needed to place an order from a single warehouse.</summary>
public sealed record PlaceOrderCommand(int CustomerId, int WarehouseId, IReadOnlyList<OrderLine> Lines);

/// <summary>The order header returned by <c>dbo.usp_PlaceOrder</c>.</summary>
public sealed record OrderSummary
{
    public int OrderId { get; init; }
    public int CustomerId { get; init; }
    public int WarehouseId { get; init; }
    public DateTime OrderDateUtc { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
}

/// <summary>Order header from <c>dbo.usp_GetOrderDetails</c> (first result set).</summary>
public sealed record OrderHeader
{
    public int OrderId { get; init; }
    public int CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public int WarehouseId { get; init; }
    public string WarehouseCode { get; init; } = string.Empty;
    public DateTime OrderDateUtc { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
}

/// <summary>An order line from <c>dbo.usp_GetOrderDetails</c> (second result set).</summary>
public sealed record OrderDetailsLine
{
    public int ProductId { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal LineTotal { get; init; }
}

/// <summary>An order with its lines.</summary>
public sealed record OrderDetails(OrderHeader Header, IReadOnlyList<OrderDetailsLine> Lines);
