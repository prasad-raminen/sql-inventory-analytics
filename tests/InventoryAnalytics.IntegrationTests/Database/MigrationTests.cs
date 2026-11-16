using System.Text.RegularExpressions;
using Dapper;
using InventoryAnalytics.IntegrationTests.Infrastructure;
using InventoryAnalytics.Migrator;
using Microsoft.Data.SqlClient;

namespace InventoryAnalytics.IntegrationTests.Database;

/// <summary>
/// Deploys the real scripts with the real DbUp migrator. These tests use their own database (not the
/// shared fixture) because they assert exact row counts and re-run the deployment.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed partial class MigrationTests(DatabaseFixture fixture)
{
    private const string CountsSql = """
        SELECT
            (SELECT COUNT(*) FROM dbo.Categories)                          AS Categories,
            (SELECT COUNT(*) FROM dbo.Suppliers)                           AS Suppliers,
            (SELECT COUNT(*) FROM dbo.Warehouses)                          AS Warehouses,
            (SELECT COUNT(*) FROM dbo.Products)                            AS Products,
            (SELECT COUNT(*) FROM dbo.Inventory)                           AS Inventory,
            (SELECT COUNT(*) FROM dbo.Customers)                           AS Customers,
            (SELECT COUNT(*) FROM dbo.Orders)                              AS Orders,
            (SELECT COUNT(*) FROM dbo.Orders WHERE Status = 'Cancelled')   AS CancelledOrders,
            (SELECT COUNT(*) FROM dbo.OrderItems)                          AS OrderItems,
            (SELECT COUNT(*) FROM dbo.SchemaVersions)                      AS JournalEntries;
        """;

    [SqlServerFact]
    public async Task Migrate_OnSharedDatabase_CreatesEveryTableViewFunctionProcedureAndType()
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();

        var objects = (await connection.QueryAsync<(string Name, string Type)>(
            """
            SELECT CONCAT(SCHEMA_NAME(schema_id), '.', name) AS Name, RTRIM(type) AS Type FROM sys.objects
            WHERE type IN ('U', 'V', 'IF', 'P')
            UNION ALL
            SELECT CONCAT(SCHEMA_NAME(schema_id), '.', name), 'TT' FROM sys.table_types WHERE is_user_defined = 1;
            """)).ToHashSet();

        objects.Should().Contain(new[]
        {
            ("dbo.Categories", "U"), ("dbo.Suppliers", "U"), ("dbo.Warehouses", "U"), ("dbo.Products", "U"),
            ("dbo.Inventory", "U"), ("dbo.Customers", "U"), ("dbo.Orders", "U"), ("dbo.OrderItems", "U"),
            ("dbo.SchemaVersions", "U"),
            ("dbo.vw_ProductCatalog", "V"), ("dbo.vw_LowStock", "V"), ("dbo.vw_MonthlySales", "V"),
            ("dbo.fn_GetProductStock", "IF"),
            ("dbo.usp_PlaceOrder", "P"), ("dbo.usp_GetTopProducts", "P"), ("dbo.usp_SalesByCategory", "P"),
            ("dbo.usp_GetOrderDetails", "P"),
            ("dbo.OrderLineList", "TT"),
        });

        // Modules capture ANSI_NULLS / QUOTED_IDENTIFIER at CREATE time; both must be ON for the
        // procedures to write to tables with filtered indexes or indexes on computed columns.
        var moduleSettings = await connection.QueryAsync<(string Name, bool UsesAnsiNulls, bool UsesQuotedIdentifier)>(
            """
            SELECT OBJECT_NAME(object_id) AS Name, uses_ansi_nulls AS UsesAnsiNulls, uses_quoted_identifier AS UsesQuotedIdentifier
            FROM sys.sql_modules
            WHERE OBJECTPROPERTY(object_id, 'IsMSShipped') = 0;
            """);
        moduleSettings.Should().NotBeEmpty()
            .And.OnlyContain(m => m.UsesAnsiNulls && m.UsesQuotedIdentifier);

        bool filteredIndexExists = await connection.QuerySingleAsync<bool>(
            """
            SELECT CAST(COUNT(*) AS BIT) FROM sys.indexes
            WHERE name = 'IX_Products_Active_Name' AND has_filter = 1 AND object_id = OBJECT_ID('dbo.Products');
            """);
        filteredIndexExists.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task Migrate_FreshDatabaseWithSeed_LoadsDeterministicDemoDataAndIsIdempotent()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync(seed: true);

        TableCounts afterFirstRun = await GetCountsAsync(database);
        afterFirstRun.Should().Be(new TableCounts(
            Categories: 12, Suppliers: 4, Warehouses: 3, Products: 16, Inventory: 48, Customers: 20,
            Orders: 600, CancelledOrders: 20, OrderItems: 1200, JournalEntries: 4));

        // Second deployment: journaled migrations and seed are skipped, programmability is re-applied.
        MigrationResult rerun = await Task.Run(() =>
            DatabaseMigrator.Migrate(database.ConnectionString, TestDatabase.ScriptsRoot, seed: true));
        rerun.Successful.Should().BeTrue("a second deployment must succeed, but it failed with {0}", rerun.Error);
        (await GetCountsAsync(database)).Should().Be(afterFirstRun);

        // The seed script itself is idempotent too (it is safe to run by hand, outside the journal).
        await ExecuteScriptAsync(database, Path.Combine(TestDatabase.ScriptsRoot, "seed", "0900_SeedDemoData.sql"));
        (await GetCountsAsync(database)).Should().Be(afterFirstRun);
    }

    [SqlServerFact]
    public async Task Migrate_WithoutSeed_CreatesAnEmptySchema()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync(seed: false);

        TableCounts counts = await GetCountsAsync(database);

        counts.Should().Be(new TableCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, JournalEntries: 3));
    }

    private static async Task<TableCounts> GetCountsAsync(TestDatabase database)
    {
        await using SqlConnection connection = await database.OpenConnectionAsync();
        return await connection.QuerySingleAsync<TableCounts>(CountsSql);
    }

    /// <summary>Runs a script the way sqlcmd would: batches separated by lines containing only GO.</summary>
    private static async Task ExecuteScriptAsync(TestDatabase database, string path)
    {
        string script = await File.ReadAllTextAsync(path);
        await using SqlConnection connection = await database.OpenConnectionAsync();
        foreach (string batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await connection.ExecuteAsync(batch, commandTimeout: 120);
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    public sealed record TableCounts(
        int Categories, int Suppliers, int Warehouses, int Products, int Inventory, int Customers,
        int Orders, int CancelledOrders, int OrderItems, int JournalEntries);
}
