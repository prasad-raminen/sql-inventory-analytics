using System.Data;
using System.Data.Common;
using Dapper;
using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace InventoryAnalytics.Infrastructure.Repositories;

public sealed class ReportRepository(IDbConnectionFactory connectionFactory, IOptions<DatabaseOptions> options)
    : IReportRepository
{
    private readonly int _timeout = options.Value.CommandTimeoutSeconds;

    public async Task<IReadOnlyList<LowStockItem>> GetLowStockAsync(int? warehouseId, CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT ProductId, Sku, ProductName, CategoryName, WarehouseId, WarehouseCode, WarehouseName,
                   QuantityOnHand, ReorderLevel, SuggestedReorderQty, SupplierId, SupplierName, SupplierEmail
            FROM dbo.vw_LowStock
            {(warehouseId is null ? string.Empty : "WHERE WarehouseId = @WarehouseId")}
            ORDER BY SuggestedReorderQty DESC, Sku, WarehouseCode;
            """;

        return await QueryListAsync<LowStockItem>(sql, new { WarehouseId = warehouseId }, CommandType.Text, cancellationToken);
    }

    public async Task<IReadOnlyList<MonthlySales>> GetMonthlySalesAsync(int? year, CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT [Year], [Month], MonthStart, OrderCount, UniqueCustomers, UnitsSold,
                   Revenue, AverageOrderValue, RevenueChangePct
            FROM dbo.vw_MonthlySales
            {(year is null ? string.Empty : "WHERE [Year] = @Year")}
            ORDER BY MonthStart;
            """;

        return await QueryListAsync<MonthlySales>(sql, new { Year = year }, CommandType.Text, cancellationToken);
    }

    public async Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(
        ReportPeriod period, int top, int? categoryId, CancellationToken cancellationToken)
    {
        DynamicParameters parameters = PeriodParameters(period);
        parameters.Add("Top", top, DbType.Int32);
        parameters.Add("CategoryId", categoryId, DbType.Int32);

        return await QueryListAsync<TopProduct>("dbo.usp_GetTopProducts", parameters, CommandType.StoredProcedure, cancellationToken);
    }

    public async Task<IReadOnlyList<CategorySales>> GetSalesByCategoryAsync(
        ReportPeriod period, bool rollUpToRoot, CancellationToken cancellationToken)
    {
        DynamicParameters parameters = PeriodParameters(period);
        parameters.Add("RollUpToRoot", rollUpToRoot, DbType.Boolean);

        return await QueryListAsync<CategorySales>("dbo.usp_SalesByCategory", parameters, CommandType.StoredProcedure, cancellationToken);
    }

    private static DynamicParameters PeriodParameters(ReportPeriod period)
    {
        // DbType.DateTime2 matches the procedures' DATETIME2(3) parameters exactly, avoiding
        // the legacy DATETIME type (and its 3.33 ms rounding) that DateTime maps to by default.
        var parameters = new DynamicParameters();
        parameters.Add("FromUtc", period.FromUtc, DbType.DateTime2);
        parameters.Add("ToUtc", period.ToUtcExclusive, DbType.DateTime2);
        return parameters;
    }

    private async Task<IReadOnlyList<T>> QueryListAsync<T>(
        string sql, object parameters, CommandType commandType, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        try
        {
            IEnumerable<T> rows = await connection.QueryAsync<T>(
                new CommandDefinition(sql, parameters, commandType: commandType, commandTimeout: _timeout, cancellationToken: cancellationToken));
            return rows.AsList();
        }
        catch (SqlException ex) when (SqlErrorTranslator.Translate(ex.Number, ex.Message, ex) is { } domainError)
        {
            throw domainError;
        }
    }
}
