using System.Data;
using Dapper;
using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Infrastructure.Data;
using InventoryAnalytics.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace InventoryAnalytics.IntegrationTests.Database;

/// <summary>Calls <c>dbo.usp_PlaceOrder</c> directly over ADO.NET to pin down its contract.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class PlaceOrderProcedureTests(DatabaseFixture fixture)
{
    private readonly TestData _data = fixture.Data;

    [SqlServerFact]
    public async Task PlaceOrder_EnoughStock_DecrementsStockAndWritesOrderWithItems()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int scanner = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, unitPrice: 100.00m);
        int tripod = await _data.CreateProductAsync(s.Sku("B"), s.CategoryId, s.SupplierId, unitPrice: 25.50m);
        await _data.SetStockAsync(scanner, s.WarehouseId, 10);
        await _data.SetStockAsync(tripod, s.WarehouseId, 5);

        // Two lines for the same product are merged into one order item (3 + 1 = 4 units).
        PlacedOrder placed = await PlaceOrderAsync(s.CustomerId, s.WarehouseId, (scanner, 3), (tripod, 2), (scanner, 1));

        placed.OutputOrderId.Should().Be(placed.Row.OrderId);
        placed.Row.CustomerId.Should().Be(s.CustomerId);
        placed.Row.WarehouseId.Should().Be(s.WarehouseId);
        placed.Row.Status.Should().Be("Placed");
        placed.Row.TotalAmount.Should().Be(4 * 100.00m + 2 * 25.50m);
        placed.Row.OrderDateUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));

        (await _data.GetStockAsync(scanner, s.WarehouseId)).Should().Be(6);
        (await _data.GetStockAsync(tripod, s.WarehouseId)).Should().Be(3);

        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        var items = (await connection.QueryAsync<(string Sku, int Quantity, decimal UnitPrice, decimal LineTotal)>(
            """
            SELECT p.Sku, oi.Quantity, oi.UnitPrice, oi.LineTotal
            FROM dbo.OrderItems AS oi
            JOIN dbo.Products   AS p ON p.ProductId = oi.ProductId
            WHERE oi.OrderId = @OrderId
            ORDER BY p.Sku;
            """,
            new { placed.Row.OrderId })).ToList();

        items.Should().Equal(
            (s.Sku("A"), 4, 100.00m, 400.00m),
            (s.Sku("B"), 2, 25.50m, 51.00m));

        int ordersForCustomer = await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.Orders WHERE CustomerId = @CustomerId;", new { s.CustomerId });
        ordersForCustomer.Should().Be(1);
    }

    [SqlServerFact]
    public async Task PlaceOrder_InsufficientStock_Throws50010AndRollsEverythingBack()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int plentiful = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, unitPrice: 10m);
        int scarce = await _data.CreateProductAsync(s.Sku("B"), s.CategoryId, s.SupplierId, unitPrice: 20m);
        await _data.SetStockAsync(plentiful, s.WarehouseId, 10);
        await _data.SetStockAsync(scarce, s.WarehouseId, 1);

        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        int orderCountBefore = await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.Orders;");

        // The single UPDATE decrements the plentiful line before the shortfall is detected, so an
        // unchanged stock level for it proves the whole transaction was rolled back.
        Func<Task> act = () => PlaceOrderAsync(connection, s.CustomerId, s.WarehouseId, (plentiful, 3), (scarce, 2));

        SqlException error = (await act.Should().ThrowAsync<SqlException>()).Which;
        error.Number.Should().Be(SqlErrorTranslator.InsufficientStock);
        error.Message.Should().Be(
            $"Insufficient stock for product {scarce} in warehouse {s.WarehouseId}: requested 2, available 1.");
        SqlErrorTranslator.Translate(error.Number, error.Message).Should().BeOfType<InsufficientStockException>();

        (await connection.QuerySingleAsync<int>("SELECT @@TRANCOUNT;")).Should().Be(0, "no transaction may be left open");
        (await _data.GetStockAsync(plentiful, s.WarehouseId)).Should().Be(10);
        (await _data.GetStockAsync(scarce, s.WarehouseId)).Should().Be(1);
        (await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.Orders;")).Should().Be(orderCountBefore);
        (await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.OrderItems WHERE ProductId IN (@plentiful, @scarce);", new { plentiful, scarce }))
            .Should().Be(0);
    }

    [SqlServerFact]
    public async Task PlaceOrder_NoInventoryRowInWarehouse_ReportsZeroAvailable()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int unstocked = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, unitPrice: 10m);

        Func<Task> act = () => PlaceOrderAsync(s.CustomerId, s.WarehouseId, (unstocked, 1));

        SqlException error = (await act.Should().ThrowAsync<SqlException>()).Which;
        error.Number.Should().Be(SqlErrorTranslator.InsufficientStock);
        error.Message.Should().EndWith("requested 1, available 0.");
        (await _data.GetStockAsync(unstocked, s.WarehouseId)).Should().BeNull();
    }

    [SqlServerFact]
    public async Task PlaceOrder_InvalidInput_ThrowsTheDocumentedErrorNumbers()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int active = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, unitPrice: 10m);
        int retired = await _data.CreateProductAsync(s.Sku("R"), s.CategoryId, s.SupplierId, unitPrice: 10m, isActive: false);
        int closedWarehouse = await _data.CreateWarehouseAsync($"X{s.Token}", isActive: false);
        await _data.SetStockAsync(active, s.WarehouseId, 10);
        await _data.SetStockAsync(retired, s.WarehouseId, 10);
        int unknownCustomer = await _data.ScalarAsync<int>("SELECT ISNULL(MAX(CustomerId), 0) + 1000 FROM dbo.Customers;");

        await ExpectErrorAsync(SqlErrorTranslator.CustomerNotFound, () => PlaceOrderAsync(unknownCustomer, s.WarehouseId, (active, 1)));
        await ExpectErrorAsync(SqlErrorTranslator.WarehouseNotFound, () => PlaceOrderAsync(s.CustomerId, closedWarehouse, (active, 1)));
        await ExpectErrorAsync(SqlErrorTranslator.EmptyOrder, () => PlaceOrderAsync(s.CustomerId, s.WarehouseId));
        await ExpectErrorAsync(SqlErrorTranslator.ProductNotFound, () => PlaceOrderAsync(s.CustomerId, s.WarehouseId, (retired, 1)));

        (await _data.GetStockAsync(active, s.WarehouseId)).Should().Be(10);
        (await _data.GetStockAsync(retired, s.WarehouseId)).Should().Be(10);
    }

    private static async Task ExpectErrorAsync(int expectedNumber, Func<Task> act)
    {
        SqlException error = (await act.Should().ThrowAsync<SqlException>()).Which;
        error.Number.Should().Be(expectedNumber, "error message was: {0}", error.Message);
    }

    private async Task<PlacedOrder> PlaceOrderAsync(int customerId, int warehouseId, params (int ProductId, int Quantity)[] lines)
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        return await PlaceOrderAsync(connection, customerId, warehouseId, lines);
    }

    private static async Task<PlacedOrder> PlaceOrderAsync(
        SqlConnection connection, int customerId, int warehouseId, params (int ProductId, int Quantity)[] lines)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "dbo.usp_PlaceOrder";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.Int) { Value = customerId });
        command.Parameters.Add(new SqlParameter("@WarehouseId", SqlDbType.Int) { Value = warehouseId });
        command.Parameters.Add(new SqlParameter("@Lines", SqlDbType.Structured)
        {
            TypeName = OrderLineTable.TypeName,
            Value = OrderLineTable.Create(lines.Select(l => new OrderLine(l.ProductId, l.Quantity))),
        });
        var orderIdParameter = new SqlParameter("@OrderId", SqlDbType.Int) { Direction = ParameterDirection.Output };
        command.Parameters.Add(orderIdParameter);

        OrderSummary row;
        await using (SqlDataReader reader = await command.ExecuteReaderAsync())
        {
            row = reader.Parse<OrderSummary>().Single();
        }

        // Output parameters are populated once the reader is closed.
        return new PlacedOrder(row, (int)orderIdParameter.Value);
    }

    private sealed record PlacedOrder(OrderSummary Row, int OutputOrderId);
}
