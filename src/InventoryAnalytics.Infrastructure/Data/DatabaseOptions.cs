namespace InventoryAnalytics.Infrastructure.Data;

/// <summary>SQL Server connection settings, bound from <c>ConnectionStrings:InventoryDb</c> and <c>Database</c>.</summary>
public sealed class DatabaseOptions
{
    public const string ConnectionStringName = "InventoryDb";
    public const string SectionName = "Database";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Command timeout for analytics queries, in seconds (1-600).</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;
}
