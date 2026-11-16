using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace InventoryAnalytics.IntegrationTests.Infrastructure;

/// <summary>One line of an order inserted directly by a test.</summary>
public sealed record TestOrderLine(int ProductId, int Quantity, decimal UnitPrice);

/// <summary>
/// The minimum set of reference rows most tests need, all named with the same random token so they
/// never collide with seed data or with other tests.
/// </summary>
public sealed record Scenario(string Token, int SupplierId, int RootCategoryId, int CategoryId, int WarehouseId, int CustomerId)
{
    /// <summary>A SKU unique to this scenario, e.g. <c>IT-1A2B3C4D-A</c>.</summary>
    public string Sku(string suffix) => $"IT-{Token}-{suffix}";
}

/// <summary>
/// Inserts test rows and returns their keys via <c>OUTPUT INSERTED</c>, so no test depends on
/// IDENTITY ordering or on the seed having produced particular ID values. Seeded rows are looked up
/// by natural key (SKU, warehouse code, e-mail).
/// </summary>
public sealed class TestData(string connectionString)
{
    public static string NewToken() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public async Task<Scenario> CreateScenarioAsync()
    {
        string token = NewToken();
        int supplierId = await CreateSupplierAsync($"IT Supplier {token}");
        int rootCategoryId = await CreateCategoryAsync($"IT Root {token}");
        int categoryId = await CreateCategoryAsync($"IT Leaf {token}", rootCategoryId);
        int warehouseId = await CreateWarehouseAsync($"W{token}");
        int customerId = await CreateCustomerAsync(token);
        return new Scenario(token, supplierId, rootCategoryId, categoryId, warehouseId, customerId);
    }

    public Task<int> CreateCategoryAsync(string name, int? parentCategoryId = null) =>
        InsertAsync(
            "INSERT INTO dbo.Categories (Name, ParentCategoryId) OUTPUT INSERTED.CategoryId VALUES (@Name, @ParentCategoryId);",
            new { Name = name, ParentCategoryId = parentCategoryId });

    public Task<int> CreateSupplierAsync(string name) =>
        InsertAsync(
            """
            INSERT INTO dbo.Suppliers (Name, ContactEmail, Country)
            OUTPUT INSERTED.SupplierId
            VALUES (@Name, 'buyer@supplier.example', N'Testland');
            """,
            new { Name = name });

    /// <param name="code">3-10 characters, unique.</param>
    public Task<int> CreateWarehouseAsync(string code, bool isActive = true) =>
        InsertAsync(
            """
            INSERT INTO dbo.Warehouses (Code, Name, City, Country, IsActive)
            OUTPUT INSERTED.WarehouseId
            VALUES (@Code, CONCAT(N'Test warehouse ', @Code), N'Testville', N'Testland', @IsActive);
            """,
            new { Code = new DbString { Value = code, IsAnsi = true, Length = 10 }, IsActive = isActive });

    public Task<int> CreateCustomerAsync(string token, string firstName = "Test", string lastName = "Customer") =>
        InsertAsync(
            """
            INSERT INTO dbo.Customers (Email, FirstName, LastName, City, Country)
            OUTPUT INSERTED.CustomerId
            VALUES (@Email, @FirstName, @LastName, N'Testville', N'Testland');
            """,
            new
            {
                Email = new DbString { Value = $"it.{token.ToLowerInvariant()}.{NewToken().ToLowerInvariant()}@example.com", IsAnsi = true, Length = 254 },
                FirstName = firstName,
                LastName = lastName,
            });

    public Task<int> CreateProductAsync(
        string sku, int categoryId, int supplierId, decimal unitPrice, int reorderLevel = 10, bool isActive = true) =>
        InsertAsync(
            """
            INSERT INTO dbo.Products (Sku, Name, CategoryId, SupplierId, UnitPrice, ReorderLevel, IsActive)
            OUTPUT INSERTED.ProductId
            VALUES (@Sku, CONCAT(N'Test product ', @Sku), @CategoryId, @SupplierId, @UnitPrice, @ReorderLevel, @IsActive);
            """,
            new
            {
                Sku = new DbString { Value = sku, IsAnsi = true, Length = 32 },
                CategoryId = categoryId,
                SupplierId = supplierId,
                UnitPrice = unitPrice,
                ReorderLevel = reorderLevel,
                IsActive = isActive,
            });

    public async Task SetStockAsync(int productId, int warehouseId, int quantity)
    {
        await using SqlConnection connection = await OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO dbo.Inventory (ProductId, WarehouseId, QuantityOnHand) VALUES (@ProductId, @WarehouseId, @Quantity);",
            new { ProductId = productId, WarehouseId = warehouseId, Quantity = quantity });
    }

    public async Task<int?> GetStockAsync(int productId, int warehouseId)
    {
        await using SqlConnection connection = await OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT QuantityOnHand FROM dbo.Inventory WHERE ProductId = @ProductId AND WarehouseId = @WarehouseId;",
            new { ProductId = productId, WarehouseId = warehouseId });
    }

    /// <summary>Looks a product up by its natural key (works for seeded and test products alike).</summary>
    public Task<int> GetProductIdAsync(string sku) =>
        ScalarAsync<int>("SELECT ProductId FROM dbo.Products WHERE Sku = @Sku;", new { Sku = new DbString { Value = sku, IsAnsi = true, Length = 32 } });

    public Task<int> GetWarehouseIdAsync(string code) =>
        ScalarAsync<int>("SELECT WarehouseId FROM dbo.Warehouses WHERE Code = @Code;", new { Code = new DbString { Value = code, IsAnsi = true, Length = 10 } });

    /// <summary>
    /// Inserts a historical order directly (bypassing usp_PlaceOrder, which always stamps "now"), so
    /// analytics tests control the order date and status. Stock is intentionally not touched.
    /// </summary>
    public async Task<int> InsertOrderAsync(
        int customerId, int warehouseId, DateTime orderDateUtc, IReadOnlyList<TestOrderLine> lines, string status = "Delivered")
    {
        await using SqlConnection connection = await OpenAsync();
        await using SqlTransaction transaction = connection.BeginTransaction();

        var header = new DynamicParameters();
        header.Add("CustomerId", customerId, DbType.Int32);
        header.Add("WarehouseId", warehouseId, DbType.Int32);
        header.Add("OrderDateUtc", orderDateUtc, DbType.DateTime2);
        header.Add("Status", status, DbType.AnsiString, size: 20);
        header.Add("TotalAmount", lines.Sum(l => l.Quantity * l.UnitPrice), DbType.Decimal, precision: 19, scale: 4);

        int orderId = await connection.QuerySingleAsync<int>(
            """
            INSERT INTO dbo.Orders (CustomerId, WarehouseId, OrderDateUtc, Status, TotalAmount)
            OUTPUT INSERTED.OrderId
            VALUES (@CustomerId, @WarehouseId, @OrderDateUtc, @Status, @TotalAmount);
            """,
            header,
            transaction);

        await connection.ExecuteAsync(
            "INSERT INTO dbo.OrderItems (OrderId, ProductId, Quantity, UnitPrice) VALUES (@OrderId, @ProductId, @Quantity, @UnitPrice);",
            lines.Select(l => new { OrderId = orderId, l.ProductId, l.Quantity, l.UnitPrice }),
            transaction);

        await transaction.CommitAsync();
        return orderId;
    }

    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using SqlConnection connection = await OpenAsync();
        return await connection.QuerySingleAsync<T>(sql, parameters);
    }

    private async Task<int> InsertAsync(string sql, object parameters)
    {
        await using SqlConnection connection = await OpenAsync();
        return await connection.QuerySingleAsync<int>(sql, parameters);
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }
}
