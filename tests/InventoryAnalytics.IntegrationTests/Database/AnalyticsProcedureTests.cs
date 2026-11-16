using System.Data;
using Dapper;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Infrastructure.Data;
using InventoryAnalytics.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace InventoryAnalytics.IntegrationTests.Database;

/// <summary>Ranking and roll-up behaviour of <c>usp_GetTopProducts</c> and <c>usp_SalesByCategory</c>.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class AnalyticsProcedureTests(DatabaseFixture fixture)
{
    private readonly TestData _data = fixture.Data;

    [SqlServerFact]
    public async Task GetTopProducts_RanksByRevenueKeepsTiesAndComputesWindowColumns()
    {
        DateTime from = ReportWindows.TopProductsRanking;
        DateTime to = from.AddMonths(1);

        Scenario s = await _data.CreateScenarioAsync();
        int otherCategory = await _data.CreateCategoryAsync($"IT Leaf2 {s.Token}", s.RootCategoryId);
        int otherCustomer = await _data.CreateCustomerAsync(s.Token);
        int p1 = await _data.CreateProductAsync(s.Sku("P1"), s.CategoryId, s.SupplierId, 1000m);
        int p2 = await _data.CreateProductAsync(s.Sku("P2"), s.CategoryId, s.SupplierId, 500m);
        int p3 = await _data.CreateProductAsync(s.Sku("P3"), otherCategory, s.SupplierId, 250m);
        int p4 = await _data.CreateProductAsync(s.Sku("P4"), otherCategory, s.SupplierId, 100m);
        int p5 = await _data.CreateProductAsync(s.Sku("P5"), otherCategory, s.SupplierId, 10000m);

        // Revenue: P1 = 3 x 1000 = 3000, P2 = 4 x 500 = 2000, P3 = 8 x 250 = 2000, P4 = 5 x 100 = 500.
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, from.AddDays(2), [new(p1, 2, 1000m), new(p2, 4, 500m)]);
        await _data.InsertOrderAsync(otherCustomer, s.WarehouseId, from.AddDays(10), [new(p1, 1, 1000m), new(p3, 8, 250m)]);
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, from.AddDays(20), [new(p4, 5, 100m)]);
        // Excluded: cancelled, just before the window, and exactly at the (exclusive) end of it.
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, from.AddDays(5), [new(p5, 1, 10000m)], status: "Cancelled");
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, from.AddSeconds(-1), [new(p4, 100, 100m)]);
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, to, [new(p4, 100, 100m)]);

        IReadOnlyList<TopProduct> all = await GetTopProductsAsync(from, to, top: 10);

        // Rows come back ordered by rank; P2 and P3 tie, so their relative order is not asserted.
        all.Select(r => r.RevenueRank).Should().Equal(1L, 2L, 2L, 4L);
        all.Should().BeEquivalentTo(
            new[]
            {
                new { ProductId = p1, UnitsSold = 3, OrderCount = 2, Revenue = 3000m, RevenueRank = 1L, UnitsRank = 4L, RankInCategory = 1L, RevenueSharePct = (decimal?)40.00m },
                new { ProductId = p2, UnitsSold = 4, OrderCount = 1, Revenue = 2000m, RevenueRank = 2L, UnitsRank = 3L, RankInCategory = 2L, RevenueSharePct = (decimal?)26.67m },
                new { ProductId = p3, UnitsSold = 8, OrderCount = 1, Revenue = 2000m, RevenueRank = 2L, UnitsRank = 1L, RankInCategory = 1L, RevenueSharePct = (decimal?)26.67m },
                new { ProductId = p4, UnitsSold = 5, OrderCount = 1, Revenue = 500m, RevenueRank = 4L, UnitsRank = 2L, RankInCategory = 2L, RevenueSharePct = (decimal?)6.67m },
            });

        // RANK() leaves a gap after the tie, and filtering on the rank never cuts a tie in half:
        // @Top = 2 returns three products.
        IReadOnlyList<TopProduct> topTwo = await GetTopProductsAsync(from, to, top: 2);
        topTwo.Select(r => r.ProductId).Should().BeEquivalentTo(new[] { p1, p2, p3 });
    }

    [SqlServerFact]
    public async Task GetTopProducts_CategoryFilter_RanksWithinTheCategoryOnly()
    {
        DateTime from = ReportWindows.TopProductsByCategory;
        DateTime to = from.AddMonths(1);

        Scenario s = await _data.CreateScenarioAsync();
        int otherCategory = await _data.CreateCategoryAsync($"IT Leaf2 {s.Token}", s.RootCategoryId);
        int inCategory = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 100m);
        int elsewhere = await _data.CreateProductAsync(s.Sku("B"), otherCategory, s.SupplierId, 900m);
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, from.AddDays(1), [new(inCategory, 1, 100m), new(elsewhere, 1, 900m)]);

        IReadOnlyList<TopProduct> result = await GetTopProductsAsync(from, to, top: 5, categoryId: s.CategoryId);

        TopProduct only = result.Should().ContainSingle().Subject;
        only.ProductId.Should().Be(inCategory);
        only.CategoryName.Should().Be($"IT Leaf {s.Token}");
        only.RevenueRank.Should().Be(1);
        only.RevenueSharePct.Should().Be(100.00m);
    }

    [SqlServerFact]
    public async Task GetTopProducts_InvalidArguments_ThrowTheDocumentedErrorNumbers()
    {
        DateTime from = ReportWindows.TopProductsByCategory;

        Func<Task> badTop = () => GetTopProductsAsync(from, from.AddDays(1), top: 0);
        Func<Task> badRange = () => GetTopProductsAsync(from, from, top: 5);

        (await badTop.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(SqlErrorTranslator.InvalidTop);
        (await badRange.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(SqlErrorTranslator.InvalidDateRange);
    }

    [SqlServerFact]
    public async Task SalesByCategory_RollsLeafCategoriesUpToTheirRootWithParetoShares()
    {
        DateTime from = ReportWindows.SalesByCategory;
        DateTime to = from.AddMonths(1);

        Scenario s = await _data.CreateScenarioAsync();
        string rootName = $"IT Root {s.Token}";
        string leafName = $"IT Leaf {s.Token}";
        string siblingName = $"IT Sibling {s.Token}";
        string otherRootName = $"IT Other {s.Token}";
        int sibling = await _data.CreateCategoryAsync(siblingName, s.RootCategoryId);
        int otherRoot = await _data.CreateCategoryAsync(otherRootName);

        int leafProduct = await _data.CreateProductAsync(s.Sku("L"), s.CategoryId, s.SupplierId, 100m);
        int siblingProduct = await _data.CreateProductAsync(s.Sku("S"), sibling, s.SupplierId, 300m);
        int rootProduct = await _data.CreateProductAsync(s.Sku("O"), otherRoot, s.SupplierId, 50m);

        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, from.AddDays(1), [new(leafProduct, 2, 100m), new(siblingProduct, 1, 300m)]);
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, from.AddDays(2), [new(siblingProduct, 1, 300m)]);
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, from.AddDays(3), [new(rootProduct, 4, 50m)]);
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, from.AddDays(4), [new(leafProduct, 50, 100m)], status: "Cancelled");

        // Rolled up: root = 200 + 600 = 800 over 2 orders; other root = 200 over 1 order.
        IReadOnlyList<CategorySales> rolledUp = await GetSalesByCategoryAsync(from, to, rollUpToRoot: true);

        rolledUp.Should().BeEquivalentTo(
            new[]
            {
                new { CategoryId = s.RootCategoryId, CategoryName = rootName, UnitsSold = 4, OrderCount = 2, DistinctProducts = 2,
                      Revenue = 800m, RevenuePerOrder = (decimal?)400m, RevenueSharePct = (decimal?)80m, CumulativeSharePct = (decimal?)80m },
                new { CategoryId = otherRoot, CategoryName = otherRootName, UnitsSold = 4, OrderCount = 1, DistinctProducts = 1,
                      Revenue = 200m, RevenuePerOrder = (decimal?)200m, RevenueSharePct = (decimal?)20m, CumulativeSharePct = (decimal?)100m },
            },
            options => options.WithStrictOrdering());

        // Leaf level: sibling 600, leaf 200, other root 200 (the tie is broken by CategoryId).
        IReadOnlyList<CategorySales> leaves = await GetSalesByCategoryAsync(from, to, rollUpToRoot: false);

        leaves.Select(c => c.CategoryName).Should().BeEquivalentTo(siblingName, leafName, otherRootName);
        leaves[0].Should().BeEquivalentTo(new { CategoryId = sibling, Revenue = 600m, UnitsSold = 2, OrderCount = 2, RevenueSharePct = (decimal?)60m });
        leaves.Select(c => c.CumulativeSharePct).Should().Equal(60m, 80m, 100m);
        leaves.Select(c => c.RevenueSharePct).Should().Equal(60m, 20m, 20m);
    }

    private async Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(DateTime from, DateTime to, int top, int? categoryId = null)
    {
        var parameters = new DynamicParameters();
        parameters.Add("FromUtc", from, DbType.DateTime2);
        parameters.Add("ToUtc", to, DbType.DateTime2);
        parameters.Add("Top", top, DbType.Int32);
        parameters.Add("CategoryId", categoryId, DbType.Int32);

        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        return (await connection.QueryAsync<TopProduct>("dbo.usp_GetTopProducts", parameters, commandType: CommandType.StoredProcedure)).AsList();
    }

    private async Task<IReadOnlyList<CategorySales>> GetSalesByCategoryAsync(DateTime from, DateTime to, bool rollUpToRoot)
    {
        var parameters = new DynamicParameters();
        parameters.Add("FromUtc", from, DbType.DateTime2);
        parameters.Add("ToUtc", to, DbType.DateTime2);
        parameters.Add("RollUpToRoot", rollUpToRoot, DbType.Boolean);

        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        return (await connection.QueryAsync<CategorySales>("dbo.usp_SalesByCategory", parameters, commandType: CommandType.StoredProcedure)).AsList();
    }
}
