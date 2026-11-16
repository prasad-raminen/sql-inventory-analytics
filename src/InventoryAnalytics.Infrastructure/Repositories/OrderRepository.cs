using System.Data;
using System.Data.Common;
using Dapper;
using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace InventoryAnalytics.Infrastructure.Repositories;

public sealed class OrderRepository(IDbConnectionFactory connectionFactory, IOptions<DatabaseOptions> options)
    : IOrderRepository
{
    private readonly int _timeout = options.Value.CommandTimeoutSeconds;

    public async Task<OrderSummary> PlaceOrderAsync(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var parameters = new DynamicParameters();
        parameters.Add("CustomerId", command.CustomerId, DbType.Int32);
        parameters.Add("WarehouseId", command.WarehouseId, DbType.Int32);
        // All lines travel in one round trip as a table-valued parameter.
        parameters.Add("Lines", OrderLineTable.Create(command.Lines).AsTableValuedParameter(OrderLineTable.TypeName));

        await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        try
        {
            // The procedure owns the transaction (XACT_ABORT + TRY/CATCH + THROW), so no client-side
            // transaction is opened here: a failure is already fully rolled back when we see it.
            return await connection.QuerySingleAsync<OrderSummary>(new CommandDefinition(
                "dbo.usp_PlaceOrder",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: _timeout,
                cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (SqlErrorTranslator.Translate(ex.Number, ex.Message, ex) is { } domainError)
        {
            throw domainError;
        }
    }

    public async Task<OrderDetails?> GetOrderAsync(int orderId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            "dbo.usp_GetOrderDetails",
            new { OrderId = orderId },
            commandType: CommandType.StoredProcedure,
            commandTimeout: _timeout,
            cancellationToken: cancellationToken));

        OrderHeader? header = await results.ReadSingleOrDefaultAsync<OrderHeader>();
        if (header is null)
        {
            return null;
        }

        IEnumerable<OrderDetailsLine> lines = await results.ReadAsync<OrderDetailsLine>();
        return new OrderDetails(header, lines.AsList());
    }
}
