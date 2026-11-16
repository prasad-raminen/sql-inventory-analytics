using InventoryAnalytics.Migrator;
using Microsoft.Data.SqlClient;

namespace InventoryAnalytics.IntegrationTests.Infrastructure;

/// <summary>
/// A uniquely named, throwaway database deployed with the real <see cref="DatabaseMigrator"/> and
/// the scripts from <c>/database</c>. Disposing it drops the database.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _masterConnectionString;

    private TestDatabase(string name, string connectionString, string masterConnectionString)
    {
        Name = name;
        ConnectionString = connectionString;
        _masterConnectionString = masterConnectionString;
    }

    /// <summary>The scripts copied next to the test assembly by the Migrator project reference.</summary>
    public static string ScriptsRoot { get; } = Path.Combine(AppContext.BaseDirectory, "database");

    public string Name { get; }

    public string ConnectionString { get; }

    /// <summary>Creates a fresh database and applies every migration (and optionally the demo seed).</summary>
    public static async Task<TestDatabase> CreateAsync(bool seed)
    {
        string serverConnectionString = SqlServerTestSettings.ConnectionString
            ?? throw new InvalidOperationException($"{SqlServerTestSettings.ConnectionStringVariable} is not set.");

        string name = $"InventoryAnalytics_IT_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        string connectionString = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = name }.ConnectionString;
        string masterConnectionString = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" }.ConnectionString;

        var database = new TestDatabase(name, connectionString, masterConnectionString);
        MigrationResult result = await Task.Run(() => DatabaseMigrator.Migrate(connectionString, ScriptsRoot, seed));
        if (!result.Successful)
        {
            await database.DisposeAsync();
            throw new InvalidOperationException(
                $"Migration failed in step '{result.FailedStep}', script '{result.FailedScript}'.", result.Error);
        }

        return database;
    }

    public async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        // Pooled connections would otherwise keep the database in use.
        using (var pooled = new SqlConnection(ConnectionString))
        {
            SqlConnection.ClearPool(pooled);
        }

        string quotedName = "[" + Name.Replace("]", "]]", StringComparison.Ordinal) + "]";

        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();
        await using SqlCommand command = master.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(@Name) IS NOT NULL
            BEGIN
                ALTER DATABASE {quotedName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quotedName};
            END;
            """;
        command.Parameters.Add(new SqlParameter("@Name", System.Data.SqlDbType.NVarChar, 128) { Value = Name });
        await command.ExecuteNonQueryAsync();
    }
}
