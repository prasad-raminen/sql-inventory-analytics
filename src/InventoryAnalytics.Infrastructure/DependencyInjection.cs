using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Infrastructure.Data;
using InventoryAnalytics.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryAnalytics.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the SQL Server connection factory and Dapper repositories. The connection string is
    /// read from <c>ConnectionStrings:InventoryDb</c> and validated when the host starts, so a
    /// misconfigured deployment fails fast instead of on the first request.
    /// </summary>
    public static IServiceCollection AddInventoryInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                configuration.GetSection(DatabaseOptions.SectionName).Bind(options);
                options.ConnectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)
                    ?? options.ConnectionString;
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} must be configured.")
            .Validate(o => o.CommandTimeoutSeconds is >= 1 and <= 600,
                "Database:CommandTimeoutSeconds must be between 1 and 600.")
            .ValidateOnStart();

        services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IReportRepository, ReportRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();

        return services;
    }
}
