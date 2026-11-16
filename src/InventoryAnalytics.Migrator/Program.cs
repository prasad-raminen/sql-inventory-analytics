using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using DbUp.Helpers;
using DbUp.ScriptProviders;
using InventoryAnalytics.Migrator;
using Microsoft.Data.SqlClient;

// Migration strategy
// ------------------
// 1. database/migrations      -> journaled, run exactly once, in file-name order (tables, indexes, types).
// 2. database/programmability -> NOT journaled, re-run on every deploy. Every script uses CREATE OR ALTER,
//                                so views / functions / procedures always match source control.
// 3. database/seed            -> journaled demo data, only applied with --seed (never in production).

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

EnsureDatabase.For.SqlDatabase(options.ConnectionString);

var steps = new List<(string Name, UpgradeEngine Engine)>
{
    ("migrations", BuildEngine(options, "migrations", journaled: true)),
    ("programmability", BuildEngine(options, "programmability", journaled: false)),
};

if (options.Seed)
{
    steps.Add(("seed", BuildEngine(options, "seed", journaled: true)));
}

foreach (var (name, engine) in steps)
{
    Console.WriteLine($"==> Applying {name} scripts");
    DatabaseUpgradeResult result = engine.PerformUpgrade();
    if (!result.Successful)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"Failed while applying '{result.ErrorScript?.Name}': {result.Error}");
        Console.ResetColor();
        return 1;
    }
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Database is up to date.");
Console.ResetColor();
return 0;

static UpgradeEngine BuildEngine(MigratorOptions options, string folder, bool journaled)
{
    string path = Path.Combine(options.ScriptsRoot, folder);
    if (!Directory.Exists(path))
    {
        throw new DirectoryNotFoundException($"Script folder not found: {path}");
    }

    UpgradeEngineBuilder builder = DeployChanges.To
        .SqlDatabase(options.ConnectionString)
        .WithScriptsFromFileSystem(path, new FileSystemScriptOptions { IncludeSubDirectories = false })
        .WithTransactionPerScript()
        .WithExecutionTimeout(TimeSpan.FromMinutes(2))
        .LogToConsole();

    builder = journaled
        ? builder.JournalToSqlTable("dbo", "SchemaVersions")
        : builder.JournalTo(new NullJournal());

    return builder.Build();
}

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
