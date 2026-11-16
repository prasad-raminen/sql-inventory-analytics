using System.Data.Common;
using InventoryAnalytics.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace InventoryAnalytics.Api.Health;

/// <summary>Readiness probe: the API is only ready when it can reach the database.</summary>
public sealed class SqlServerHealthCheck(IDbConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("SQL Server is unreachable.", ex);
        }
    }
}
