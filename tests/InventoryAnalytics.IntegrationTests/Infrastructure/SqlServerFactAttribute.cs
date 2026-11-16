namespace InventoryAnalytics.IntegrationTests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that marks the test as skipped (not failed) when no SQL Server
/// connection string is configured in <see cref="SqlServerTestSettings.ConnectionStringVariable"/>.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerTestSettings.IsConfigured)
        {
            Skip = SqlServerTestSettings.SkipReason;
        }
    }
}
