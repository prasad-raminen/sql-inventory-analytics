using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Core.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace InventoryAnalytics.Api.Endpoints;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/reports").WithTags("Reports");

        group.MapGet("/low-stock", GetLowStock)
            .WithName("GetLowStock")
            .WithSummary("Stock at or below reorder level, with a suggested reorder quantity (vw_LowStock).")
            .ProducesValidationProblem();

        group.MapGet("/monthly-sales", GetMonthlySales)
            .WithName("GetMonthlySales")
            .WithSummary("Revenue, orders and month-over-month growth per calendar month (vw_MonthlySales).")
            .ProducesValidationProblem();

        group.MapGet("/top-products", GetTopProducts)
            .WithName("GetTopProducts")
            .WithSummary("Best sellers by revenue with RANK/DENSE_RANK/ROW_NUMBER (usp_GetTopProducts). Dates are inclusive, UTC.")
            .ProducesValidationProblem();

        group.MapGet("/sales-by-category", GetSalesByCategory)
            .WithName("GetSalesByCategory")
            .WithSummary("Revenue share and Pareto curve per category (usp_SalesByCategory). Dates are inclusive, UTC.")
            .ProducesValidationProblem();

        return routes;
    }

    private static async Task<Ok<IReadOnlyList<LowStockItem>>> GetLowStock(
        int? warehouseId, IReportService reports, CancellationToken cancellationToken) =>
        TypedResults.Ok(await reports.GetLowStockAsync(warehouseId, cancellationToken));

    private static async Task<Ok<IReadOnlyList<MonthlySales>>> GetMonthlySales(
        int? year, IReportService reports, CancellationToken cancellationToken) =>
        TypedResults.Ok(await reports.GetMonthlySalesAsync(year, cancellationToken));

    private static async Task<Ok<PeriodReport<TopProduct>>> GetTopProducts(
        DateOnly? from, DateOnly? to, int? top, int? categoryId, IReportService reports, CancellationToken cancellationToken) =>
        TypedResults.Ok(await reports.GetTopProductsAsync(from, to, top, categoryId, cancellationToken));

    private static async Task<Ok<PeriodReport<CategorySales>>> GetSalesByCategory(
        DateOnly? from, DateOnly? to, bool? rollUp, IReportService reports, CancellationToken cancellationToken) =>
        TypedResults.Ok(await reports.GetSalesByCategoryAsync(from, to, rollUp ?? true, cancellationToken));
}
