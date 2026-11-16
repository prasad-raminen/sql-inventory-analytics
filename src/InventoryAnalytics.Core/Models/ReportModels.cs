namespace InventoryAnalytics.Core.Models;

/// <summary>A (product, warehouse) pair at or below its reorder level (<c>dbo.vw_LowStock</c>).</summary>
public sealed record LowStockItem
{
    public int ProductId { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string CategoryName { get; init; } = string.Empty;
    public int WarehouseId { get; init; }
    public string WarehouseCode { get; init; } = string.Empty;
    public string WarehouseName { get; init; } = string.Empty;
    public int QuantityOnHand { get; init; }
    public int ReorderLevel { get; init; }
    public int SuggestedReorderQty { get; init; }
    public int SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public string? SupplierEmail { get; init; }
}

/// <summary>One calendar month of sales (<c>dbo.vw_MonthlySales</c>).</summary>
public sealed record MonthlySales
{
    public int Year { get; init; }
    public int Month { get; init; }
    public DateTime MonthStart { get; init; }
    public int OrderCount { get; init; }
    public int UniqueCustomers { get; init; }
    public int UnitsSold { get; init; }
    public decimal Revenue { get; init; }
    public decimal AverageOrderValue { get; init; }

    /// <summary>Revenue change versus the previous month in percent; null for the first month.</summary>
    public decimal? RevenueChangePct { get; init; }
}

/// <summary>A ranked best-selling product (<c>dbo.usp_GetTopProducts</c>).</summary>
public sealed record TopProduct
{
    public int ProductId { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public int UnitsSold { get; init; }
    public int OrderCount { get; init; }
    public decimal Revenue { get; init; }

    /// <summary>RANK() by revenue: ties share a rank and leave a gap.</summary>
    public long RevenueRank { get; init; }

    /// <summary>DENSE_RANK() by units sold.</summary>
    public long UnitsRank { get; init; }

    /// <summary>ROW_NUMBER() inside the product's category.</summary>
    public long RankInCategory { get; init; }

    public decimal? RevenueSharePct { get; init; }
}

/// <summary>Revenue aggregated per category (<c>dbo.usp_SalesByCategory</c>).</summary>
public sealed record CategorySales
{
    public int CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public int UnitsSold { get; init; }
    public int OrderCount { get; init; }
    public int DistinctProducts { get; init; }
    public decimal Revenue { get; init; }
    public decimal? RevenuePerOrder { get; init; }
    public decimal? RevenueSharePct { get; init; }

    /// <summary>Running share of revenue ordered by revenue descending (Pareto curve).</summary>
    public decimal? CumulativeSharePct { get; init; }
}

/// <summary>A resolved, half-open UTC reporting window: [<see cref="FromUtc"/>, <see cref="ToUtcExclusive"/>).</summary>
public sealed record ReportPeriod(DateTime FromUtc, DateTime ToUtcExclusive);

/// <summary>A report result together with the period it covers, so clients can see resolved defaults.</summary>
public sealed record PeriodReport<T>(ReportPeriod Period, IReadOnlyList<T> Items);
