using InventoryAnalytics.Core.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace InventoryAnalytics.UnitTests.TestSupport;

/// <summary>
/// Hosts the real API pipeline in memory (routing, binding, filters, ProblemDetails, the real
/// services) with only the repositories replaced by substitutes, so no database is required.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public IProductRepository Products { get; } = Substitute.For<IProductRepository>();

    public IReportRepository Reports { get; } = Substitute.For<IReportRepository>();

    public IOrderRepository Orders { get; } = Substitute.For<IOrderRepository>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Satisfies start-up validation; nothing ever connects because repositories are substituted.
        builder.UseSetting("ConnectionStrings:InventoryDb", "Server=unused;Database=unused");
        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProductRepository>();
            services.RemoveAll<IReportRepository>();
            services.RemoveAll<IOrderRepository>();
            services.AddSingleton(Products);
            services.AddSingleton(Reports);
            services.AddSingleton(Orders);
        });
    }
}
