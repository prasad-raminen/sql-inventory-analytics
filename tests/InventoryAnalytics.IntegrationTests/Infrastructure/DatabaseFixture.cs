using InventoryAnalytics.Infrastructure.Data;
using InventoryAnalytics.Infrastructure.Repositories;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace InventoryAnalytics.IntegrationTests.Infrastructure;

/// <summary>
/// One migrated + seeded database shared by every test in <see cref="SqlServerCollection"/>.
/// Tests never assume an empty database or particular IDENTITY values: each one creates its own
/// uniquely named rows (see <see cref="TestData"/>) and, where it aggregates orders, uses its own
/// reporting window (see <see cref="ReportWindows"/>).
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private TestDatabase? _database;

    public TestDatabase Database => _database
        ?? throw new InvalidOperationException(SqlServerTestSettings.SkipReason);

    public string ConnectionString => Database.ConnectionString;

    public TestData Data => new(ConnectionString);

    public async Task InitializeAsync()
    {
        // xUnit builds collection fixtures even when every test is skipped; do nothing then.
        if (!SqlServerTestSettings.IsConfigured)
        {
            return;
        }

        _database = await TestDatabase.CreateAsync(seed: true);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    public Task<SqlConnection> OpenConnectionAsync() => Database.OpenConnectionAsync();

    public ProductRepository CreateProductRepository()
    {
        IOptions<DatabaseOptions> options = Options();
        return new ProductRepository(new SqlConnectionFactory(options), options);
    }

    public ReportRepository CreateReportRepository()
    {
        IOptions<DatabaseOptions> options = Options();
        return new ReportRepository(new SqlConnectionFactory(options), options);
    }

    public OrderRepository CreateOrderRepository()
    {
        IOptions<DatabaseOptions> options = Options();
        return new OrderRepository(new SqlConnectionFactory(options), options);
    }

    private IOptions<DatabaseOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new DatabaseOptions
        {
            ConnectionString = ConnectionString,
            CommandTimeoutSeconds = 60,
        });
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "SQL Server";
}
