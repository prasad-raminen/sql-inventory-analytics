using InventoryAnalytics.Migrator;
using Microsoft.Data.SqlClient;

// See DatabaseMigrator for the migration strategy (journaled migrations, re-applied
// programmability, optional seed). This entry point only parses options and reports the outcome.

MigratorOptions options;
try
{
    options = MigratorOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Usage: InventoryAnalytics.Migrator --connection \"<connection string>\" [--seed] [--scripts <dir>]");
    return 2;
}

if (!await WaitForServerAsync(options.ConnectionString))
{
    Console.Error.WriteLine("SQL Server did not become reachable in time.");
    return 1;
}

MigrationResult result = DatabaseMigrator.Migrate(options.ConnectionString, options.ScriptsRoot, options.Seed);
if (!result.Successful)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Failed while applying {result.FailedStep} script '{result.FailedScript}': {result.Error}");
    Console.ResetColor();
    return 1;
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Database is up to date.");
Console.ResetColor();
return 0;

static async Task<bool> WaitForServerAsync(string connectionString)
{
    // Connect to master so we can wait for the server before the target database exists.
    var masterConnection = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;

    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            await using var connection = new SqlConnection(masterConnection);
            await connection.OpenAsync();
            return true;
        }
        catch (SqlException ex)
        {
            Console.WriteLine($"Waiting for SQL Server (attempt {attempt}/30): {ex.Message}");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    return false;
}
