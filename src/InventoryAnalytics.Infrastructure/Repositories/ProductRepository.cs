using System.Data.Common;
using System.Text;
using Dapper;
using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace InventoryAnalytics.Infrastructure.Repositories;

public sealed class ProductRepository(IDbConnectionFactory connectionFactory, IOptions<DatabaseOptions> options)
    : IProductRepository
{
    private const string CatalogColumns = """
        SELECT ProductId, Sku, Name, CategoryId, CategoryName, SupplierId, SupplierName,
               UnitPrice, ReorderLevel, IsActive, TotalOnHand
        FROM dbo.vw_ProductCatalog
        """;

    private readonly int _timeout = options.Value.CommandTimeoutSeconds;

    public async Task<IReadOnlyList<Product>> GetProductsAsync(int? categoryId, bool includeInactive, CancellationToken cancellationToken)
    {
        // Only the predicates that are actually needed are appended (values stay parameterized),
        // so SQL Server caches one simple plan per shape instead of a "catch-all" plan.
        var sql = new StringBuilder(CatalogColumns).AppendLine().AppendLine("WHERE 1 = 1");
        var parameters = new DynamicParameters();

        if (categoryId is not null)
        {
            sql.AppendLine("  AND CategoryId = @CategoryId");
            parameters.Add("CategoryId", categoryId);
        }

        if (!includeInactive)
        {
            sql.AppendLine("  AND IsActive = 1");
        }

        sql.Append("ORDER BY Name;");

        await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        IEnumerable<Product> rows = await connection.QueryAsync<Product>(
            new CommandDefinition(sql.ToString(), parameters, commandTimeout: _timeout, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<Product?> GetByIdAsync(int productId, CancellationToken cancellationToken)
    {
        const string sql = CatalogColumns + "\nWHERE ProductId = @ProductId;";

        await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Product>(
            new CommandDefinition(sql, new { ProductId = productId }, commandTimeout: _timeout, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<WarehouseStock>> GetStockByWarehouseAsync(int productId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT WarehouseId, WarehouseCode, WarehouseName, City, QuantityOnHand, IsLowStock
            FROM dbo.fn_GetProductStock(@ProductId)
            ORDER BY WarehouseCode;
            """;

        await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        IEnumerable<WarehouseStock> rows = await connection.QueryAsync<WarehouseStock>(
            new CommandDefinition(sql, new { ProductId = productId }, commandTimeout: _timeout, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
