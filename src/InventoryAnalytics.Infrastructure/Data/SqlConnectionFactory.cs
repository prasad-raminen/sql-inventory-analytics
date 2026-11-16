using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace InventoryAnalytics.Infrastructure.Data;

/// <summary>Creates open database connections. Abstracted so health checks and repositories share one policy.</summary>
public interface IDbConnectionFactory
{
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Opens pooled <see cref="SqlConnection"/>s. ADO.NET pooling makes open/close per operation
/// cheap, so repositories never hold a connection longer than a single call.
/// </summary>
public sealed class SqlConnectionFactory(IOptions<DatabaseOptions> options) : IDbConnectionFactory
{
    private readonly string _connectionString = options.Value.ConnectionString;

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
