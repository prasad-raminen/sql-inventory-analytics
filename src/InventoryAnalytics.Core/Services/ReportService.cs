using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;

namespace InventoryAnalytics.Core.Services;

public interface IReportService
{
    Task<IReadOnlyList<LowStockItem>> GetLowStockAsync(int? warehouseId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonthlySales>> GetMonthlySalesAsync(int? year, CancellationToken cancellationToken = default);

    Task<PeriodReport<TopProduct>> GetTopProductsAsync(
        DateOnly? from, DateOnly? to, int? top, int? categoryId, CancellationToken cancellationToken = default);

    Task<PeriodReport<CategorySales>> GetSalesByCategoryAsync(
        DateOnly? from, DateOnly? to, bool rollUpToRoot, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates report parameters and resolves defaults before delegating to the database.
/// Dates are calendar days in UTC; <c>to</c> is inclusive for callers and converted to the
/// half-open range the stored procedures expect.
/// </summary>
public sealed class ReportService(IReportRepository reports, TimeProvider timeProvider) : IReportService
{
    public const int DefaultTop = 10;
    public const int MaxTop = 100;
    public const int MinYear = 2000;
    public const int MaxYear = 2100;

    public Task<IReadOnlyList<LowStockItem>> GetLowStockAsync(int? warehouseId, CancellationToken cancellationToken = default)
    {
        if (warehouseId is <= 0)
        {
            throw new RequestValidationException("warehouseId", "warehouseId must be a positive number.");
        }

        return reports.GetLowStockAsync(warehouseId, cancellationToken);
    }

    public Task<IReadOnlyList<MonthlySales>> GetMonthlySalesAsync(int? year, CancellationToken cancellationToken = default)
    {
        if (year is < MinYear or > MaxYear)
        {
            throw new RequestValidationException("year", $"year must be between {MinYear} and {MaxYear}.");
        }

        return reports.GetMonthlySalesAsync(year, cancellationToken);
    }

    public async Task<PeriodReport<TopProduct>> GetTopProductsAsync(
        DateOnly? from, DateOnly? to, int? top, int? categoryId, CancellationToken cancellationToken = default)
    {
        int resolvedTop = top ?? DefaultTop;
        var errors = new Dictionary<string, string[]>();

        if (resolvedTop is < 1 or > MaxTop)
        {
            errors["top"] = [$"top must be between 1 and {MaxTop}."];
        }

        if (categoryId is <= 0)
        {
            errors["categoryId"] = ["categoryId must be a positive number."];
        }

        ReportPeriod? period = TryResolvePeriod(from, to, errors);
        if (errors.Count > 0 || period is null)
        {
            throw new RequestValidationException(errors);
        }

        IReadOnlyList<TopProduct> items = await reports.GetTopProductsAsync(period, resolvedTop, categoryId, cancellationToken);
        return new PeriodReport<TopProduct>(period, items);
    }

    public async Task<PeriodReport<CategorySales>> GetSalesByCategoryAsync(
        DateOnly? from, DateOnly? to, bool rollUpToRoot, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        ReportPeriod? period = TryResolvePeriod(from, to, errors);
        if (errors.Count > 0 || period is null)
        {
            throw new RequestValidationException(errors);
        }

        IReadOnlyList<CategorySales> items = await reports.GetSalesByCategoryAsync(period, rollUpToRoot, cancellationToken);
        return new PeriodReport<CategorySales>(period, items);
    }

    /// <summary>
    /// Defaults: <c>to</c> = today (UTC); <c>from</c> = the day after the same date one year earlier,
    /// i.e. the trailing 12 months including today.
    /// </summary>
    private ReportPeriod? TryResolvePeriod(DateOnly? from, DateOnly? to, Dictionary<string, string[]> errors)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        DateOnly resolvedTo = to ?? today;
        DateOnly resolvedFrom = from ?? resolvedTo.AddYears(-1).AddDays(1);

        if (resolvedFrom > resolvedTo)
        {
            errors["from"] = ["from must be on or before to."];
            return null;
        }

        return new ReportPeriod(
            resolvedFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            resolvedTo.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }
}
