using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using DbUp.Helpers;
using DbUp.ScriptProviders;

namespace InventoryAnalytics.Migrator;

/// <summary>Outcome of a migration run. The <c>Failed*</c> properties and <see cref="Error"/> are set only on failure.</summary>
public sealed record MigrationResult(bool Successful, string? FailedStep, string? FailedScript, Exception? Error)
{
    public static MigrationResult Success { get; } = new(true, null, null, null);
}

/// <summary>
/// Applies the <c>database/</c> scripts with DbUp. Used by the console entry point and by the
/// SQL Server integration tests, so both exercise exactly the same deployment path.
/// </summary>
/// <remarks>
/// Migration strategy:
/// <list type="number">
/// <item><c>migrations</c>: journaled, run exactly once, in file-name order (tables, indexes, types).</item>
/// <item><c>programmability</c>: NOT journaled, re-run on every deploy. Every script uses
/// <c>CREATE OR ALTER</c>, so views, functions and procedures always match source control.</item>
/// <item><c>seed</c>: journaled demo data, only applied when requested (never in production).</item>
/// </list>
/// </remarks>
public static class DatabaseMigrator
{
    public const string JournalSchema = "dbo";
    public const string JournalTable = "SchemaVersions";

    /// <summary>Creates the database if it does not exist, then applies every pending script.</summary>
    /// <param name="connectionString">Connection string whose <c>Initial Catalog</c> is the target database.</param>
    /// <param name="scriptsRoot">Folder containing the <c>migrations</c>, <c>programmability</c> and <c>seed</c> sub-folders.</param>
    /// <param name="seed">When true, the demo seed scripts are applied after the schema.</param>
    public static MigrationResult Migrate(string connectionString, string scriptsRoot, bool seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsRoot);

        EnsureDatabase.For.SqlDatabase(connectionString);

        var steps = new List<(string Name, UpgradeEngine Engine)>
        {
            ("migrations", BuildEngine(connectionString, scriptsRoot, "migrations", journaled: true)),
            ("programmability", BuildEngine(connectionString, scriptsRoot, "programmability", journaled: false)),
        };

        if (seed)
        {
            steps.Add(("seed", BuildEngine(connectionString, scriptsRoot, "seed", journaled: true)));
        }

        foreach (var (name, engine) in steps)
        {
            Console.WriteLine($"==> Applying {name} scripts");
            DatabaseUpgradeResult result = engine.PerformUpgrade();
            if (!result.Successful)
            {
                return new MigrationResult(false, name, result.ErrorScript?.Name, result.Error);
            }
        }

        return MigrationResult.Success;
    }

    private static UpgradeEngine BuildEngine(string connectionString, string scriptsRoot, string folder, bool journaled)
    {
        string path = Path.Combine(scriptsRoot, folder);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Script folder not found: {path}");
        }

        UpgradeEngineBuilder builder = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsFromFileSystem(path, new FileSystemScriptOptions { IncludeSubDirectories = false })
            .WithTransactionPerScript()
            .WithExecutionTimeout(TimeSpan.FromMinutes(2))
            .LogToConsole();

        builder = journaled
            ? builder.JournalToSqlTable(JournalSchema, JournalTable)
            : builder.JournalTo(new NullJournal());

        return builder.Build();
    }
}
