namespace InventoryAnalytics.Core.Models;

/// <summary>A catalog entry as exposed by <c>dbo.vw_ProductCatalog</c>.</summary>
public sealed record Product
{
    public int ProductId { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public int SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public decimal UnitPrice { get; init; }
    public int ReorderLevel { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Units on hand summed across every warehouse.</summary>
    public int TotalOnHand { get; init; }
}

/// <summary>Stock of one product in one warehouse, from <c>dbo.fn_GetProductStock</c>.</summary>
public sealed record WarehouseStock
{
    public int WarehouseId { get; init; }
    public string WarehouseCode { get; init; } = string.Empty;
    public string WarehouseName { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public int QuantityOnHand { get; init; }
    public bool IsLowStock { get; init; }
}

/// <summary>A product together with its per-warehouse stock breakdown.</summary>
public sealed record ProductAvailability(
    int ProductId,
    string Sku,
    string Name,
    int ReorderLevel,
    int TotalOnHand,
    IReadOnlyList<WarehouseStock> Warehouses);
