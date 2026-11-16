using Dapper;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace InventoryAnalytics.IntegrationTests.Database;

/// <summary><c>vw_LowStock</c>, <c>vw_MonthlySales</c>, <c>vw_ProductCatalog</c> and <c>fn_GetProductStock</c>.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ViewAndFunctionTests(DatabaseFixture fixture)
{
    private readonly TestData _data = fixture.Data;

    [SqlServerFact]
    public async Task LowStockView_ListsActivePairsAtOrBelowReorderLevelWithSuggestion()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int closedWarehouse = await _data.CreateWarehouseAsync($"X{s.Token}", isActive: false);
        int low = await _data.CreateProductAsync(s.Sku("LOW"), s.CategoryId, s.SupplierId, 10m, reorderLevel: 10);
        int atLevel = await _data.CreateProductAsync(s.Sku("EQ"), s.CategoryId, s.SupplierId, 10m, reorderLevel: 10);
        int healthy = await _data.CreateProductAsync(s.Sku("OK"), s.CategoryId, s.SupplierId, 10m, reorderLevel: 10);
        int retired = await _data.CreateProductAsync(s.Sku("OLD"), s.CategoryId, s.SupplierId, 10m, reorderLevel: 10, isActive: false);
        await _data.SetStockAsync(low, s.WarehouseId, 4);
        await _data.SetStockAsync(atLevel, s.WarehouseId, 10);
        await _data.SetStockAsync(healthy, s.WarehouseId, 11);
        await _data.SetStockAsync(retired, s.WarehouseId, 0);
        await _data.SetStockAsync(low, closedWarehouse, 1);

        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        var rows = (await connection.QueryAsync<LowStockItem>(
            "SELECT * FROM dbo.vw_LowStock WHERE WarehouseId IN (@Open, @Closed);",
            new { Open = s.WarehouseId, Closed = closedWarehouse })).ToList();

        rows.Should().BeEquivalentTo(new[]
        {
            new { ProductId = low, Sku = s.Sku("LOW"), WarehouseId = s.WarehouseId, WarehouseCode = $"W{s.Token}",
                  QuantityOnHand = 4, ReorderLevel = 10, SuggestedReorderQty = 16, SupplierId = s.SupplierId,
                  CategoryName = $"IT Leaf {s.Token}", SupplierName = $"IT Supplier {s.Token}" },
            new { ProductId = atLevel, Sku = s.Sku("EQ"), WarehouseId = s.WarehouseId, WarehouseCode = $"W{s.Token}",
                  QuantityOnHand = 10, ReorderLevel = 10, SuggestedReorderQty = 10, SupplierId = s.SupplierId,
                  CategoryName = $"IT Leaf {s.Token}", SupplierName = $"IT Supplier {s.Token}" },
        });
    }

    [SqlServerFact]
    public async Task LowStockView_ReportsTheSeededLowStockPairs()
    {
        // Seeded rows are identified by natural keys (SKU + warehouse code), never by IDENTITY value.
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        var rows = await connection.QueryAsync<(string Sku, string WarehouseCode, int QuantityOnHand, int SuggestedReorderQty)>(
            "SELECT Sku, WarehouseCode, QuantityOnHand, SuggestedReorderQty FROM dbo.vw_LowStock WHERE Sku NOT LIKE 'IT-%';");

        rows.Should().BeEquivalentTo(new[]
        {
            ("LS-PRO-360", "AMS01", 1, 5),     // reorder level 3  -> 2 x 3  - 1
            ("MM-VHC-02", "CHI01", 0, 4),      // reorder level 2  -> 2 x 2  - 0
            ("GN-RTK-500", "PNQ01", 6, 14),    // reorder level 10 -> 2 x 10 - 6
            ("AC-BAT-LI", "AMS01", 12, 68),    // reorder level 40 -> 2 x 40 - 12
            ("AC-TRB-01", "CHI01", 30, 30),    // reorder level 30 -> exactly at the level
        });
    }

    [SqlServerFact]
    public async Task MonthlySalesView_AggregatesPerMonthExcludesCancelledAndComputesGrowth()
    {
        int year = ReportWindows.MonthlySalesYear;
        Scenario s = await _data.CreateScenarioAsync();
        int secondCustomer = await _data.CreateCustomerAsync(s.Token);
        int product = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 100m);

        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, new DateTime(year, 3, 5, 9, 0, 0), [new(product, 2, 100m)]);
        await _data.InsertOrderAsync(secondCustomer, s.WarehouseId, new DateTime(year, 3, 20, 13, 30, 0), [new(product, 3, 100m)]);
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, new DateTime(year, 3, 31, 23, 59, 59), [new(product, 1, 100m)]);
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, new DateTime(year, 4, 2, 8, 0, 0), [new(product, 9, 100m)]);
        await _data.InsertOrderAsync(secondCustomer, s.WarehouseId, new DateTime(year, 4, 15, 8, 0, 0), [new(product, 10, 100m)], status: "Cancelled");

        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        var months = (await connection.QueryAsync<MonthlySales>(
            "SELECT * FROM dbo.vw_MonthlySales WHERE [Year] = @Year ORDER BY MonthStart;", new { Year = year })).ToList();

        months.Should().HaveCount(2);
        months[0].Should().BeEquivalentTo(new
        {
            Year = year, Month = 3, MonthStart = new DateTime(year, 3, 1),
            OrderCount = 3, UniqueCustomers = 2, UnitsSold = 6, Revenue = 600m, AverageOrderValue = 200m,
        });
        months[1].Should().BeEquivalentTo(new
        {
            Year = year, Month = 4, MonthStart = new DateTime(year, 4, 1),
            OrderCount = 1, UniqueCustomers = 1, UnitsSold = 9, Revenue = 900m, AverageOrderValue = 900m,
            RevenueChangePct = (decimal?)50.00m,   // (900 - 600) / 600
        });
    }

    [SqlServerFact]
    public async Task ProductStockFunction_ReturnsEveryActiveWarehouseWithZeroForMissingStock()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int stockedLow = s.WarehouseId;
        int stockedHigh = await _data.CreateWarehouseAsync($"H{s.Token}");
        int unstocked = await _data.CreateWarehouseAsync($"U{s.Token}");
        int closed = await _data.CreateWarehouseAsync($"X{s.Token}", isActive: false);
        int product = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 10m, reorderLevel: 5);
        await _data.SetStockAsync(product, stockedLow, 3);
        await _data.SetStockAsync(product, stockedHigh, 20);
        await _data.SetStockAsync(product, closed, 50);

        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        var rows = (await connection.QueryAsync<WarehouseStock>(
            "SELECT * FROM dbo.fn_GetProductStock(@ProductId);", new { ProductId = product })).ToList();
        int activeWarehouses = await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.Warehouses WHERE IsActive = 1;");

        rows.Should().HaveCount(activeWarehouses, "the function returns one row per ACTIVE warehouse");
        rows.Should().NotContain(r => r.WarehouseId == closed);
        rows.Where(r => r.WarehouseCode.EndsWith(s.Token, StringComparison.Ordinal)).Should().BeEquivalentTo(new[]
        {
            new { WarehouseId = stockedLow, QuantityOnHand = 3, IsLowStock = true },
            new { WarehouseId = stockedHigh, QuantityOnHand = 20, IsLowStock = false },
            new { WarehouseId = unstocked, QuantityOnHand = 0, IsLowStock = true },
        });

        var unknown = await connection.QueryAsync<WarehouseStock>(
            "SELECT * FROM dbo.fn_GetProductStock(@ProductId);", new { ProductId = -1 });
        unknown.Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task ProductCatalogView_JoinsReferenceDataAndSumsStockAcrossWarehouses()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int secondWarehouse = await _data.CreateWarehouseAsync($"V{s.Token}");
        int stocked = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 12.34m, reorderLevel: 7);
        int unstocked = await _data.CreateProductAsync(s.Sku("B"), s.CategoryId, s.SupplierId, 1m);
        await _data.SetStockAsync(stocked, s.WarehouseId, 5);
        await _data.SetStockAsync(stocked, secondWarehouse, 8);

        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        var rows = await connection.QueryAsync<Product>(
            "SELECT * FROM dbo.vw_ProductCatalog WHERE ProductId IN (@stocked, @unstocked);", new { stocked, unstocked });

        rows.Should().BeEquivalentTo(new[]
        {
            new { ProductId = stocked, Sku = s.Sku("A"), CategoryId = s.CategoryId, CategoryName = $"IT Leaf {s.Token}",
                  SupplierId = s.SupplierId, SupplierName = $"IT Supplier {s.Token}", UnitPrice = 12.34m,
                  ReorderLevel = 7, IsActive = true, TotalOnHand = 13 },
            new { ProductId = unstocked, Sku = s.Sku("B"), CategoryId = s.CategoryId, CategoryName = $"IT Leaf {s.Token}",
                  SupplierId = s.SupplierId, SupplierName = $"IT Supplier {s.Token}", UnitPrice = 1m,
                  ReorderLevel = 10, IsActive = true, TotalOnHand = 0 },
        });
    }
}
