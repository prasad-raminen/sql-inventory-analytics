namespace InventoryAnalytics.Migrator;

/// <summary>
/// Command-line / environment options for the migrator.
/// </summary>
/// <param name="ConnectionString">Target database connection string.</param>
/// <param name="Seed">When true, the demo seed scripts are applied after the schema.</param>
/// <param name="ScriptsRoot">Folder containing the migrations, programmability and seed sub-folders.</param>
internal sealed record MigratorOptions(string ConnectionString, bool Seed, string ScriptsRoot)
{
    public const string ConnectionStringVariable = "ConnectionStrings__InventoryDb";
    public const string SeedVariable = "MIGRATOR_SEED";

    /// <summary>
    /// Parses <c>--connection "..."</c>, <c>--seed</c> and <c>--scripts &lt;dir&gt;</c>.
    /// Falls back to the <c>ConnectionStrings__InventoryDb</c> and <c>MIGRATOR_SEED</c> environment variables.
    /// </summary>
    public static MigratorOptions Parse(IReadOnlyList<string> args)
    {
        string? connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        bool seed = string.Equals(Environment.GetEnvironmentVariable(SeedVariable), "true", StringComparison.OrdinalIgnoreCase);
        string scriptsRoot = Path.Combine(AppContext.BaseDirectory, "database");

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Count:
                    connectionString = args[++i];
                    break;
                case "--scripts" when i + 1 < args.Count:
                    scriptsRoot = args[++i];
                    break;
                case "--seed":
                    seed = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                $"No connection string supplied. Pass --connection \"...\" or set {ConnectionStringVariable}.");
        }

        return new MigratorOptions(connectionString, seed, scriptsRoot);
    }
}
