using InventoryAnalytics.Api.Endpoints;
using InventoryAnalytics.Api.ErrorHandling;
using InventoryAnalytics.Api.Health;
using InventoryAnalytics.Core.Services;
using InventoryAnalytics.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Cross-cutting
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Inventory & Order Analytics API",
        Version = "v1",
        Description = "ASP.NET Core minimal API over SQL Server views and stored procedures (Dapper).",
    });
    string xmlDocs = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(xmlDocs))
    {
        options.IncludeXmlComments(xmlDocs);
    }
});
builder.Services.AddSingleton(TimeProvider.System);

// Application + data access
builder.Services.AddInventoryInfrastructure();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IOrderService, OrderService>();

builder.Services.AddHealthChecks()
    .AddCheck<SqlServerHealthCheck>("sqlserver", tags: ["ready"]);

WebApplication app = builder.Build();

// Unexpected exceptions become a generic 500 ProblemDetails (no stack traces leak to clients);
// empty 4xx responses (e.g. unmatched routes, bad parameter binding) also get a ProblemDetails body.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Liveness: the process is up. Readiness: the database is reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

RouteGroupBuilder api = app.MapGroup("/api").AddEndpointFilter<DomainExceptionFilter>();
api.MapProductEndpoints();
api.MapReportEndpoints();
api.MapOrderEndpoints();

app.Run();

/// <summary>Exposed so integration-style API tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
