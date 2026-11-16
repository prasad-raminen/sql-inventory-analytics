namespace InventoryAnalytics.IntegrationTests.Infrastructure;

/// <summary>Reads the SQL Server connection used by the integration tests.</summary>
public static class SqlServerTestSettings
{
    /// <summary>
    /// Environment variable holding a connection string to a SQL Server instance (any database; the
    /// tests create and drop their own). Example:
    /// <c>Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True</c>.
    /// </summary>
    public const string ConnectionStringVariable = "SQLSERVER_TEST_CONNECTION";

    public static string? ConnectionString
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(ConnectionStringVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public static bool IsConfigured => ConnectionString is not null;

    public static string SkipReason =>
        $"{ConnectionStringVariable} is not set; SQL Server integration tests are skipped.";
}
