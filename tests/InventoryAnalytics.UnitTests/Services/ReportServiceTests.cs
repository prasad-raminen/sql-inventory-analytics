using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Core.Services;
using InventoryAnalytics.UnitTests.TestSupport;

namespace InventoryAnalytics.UnitTests.Services;

public sealed class ReportServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 18, 30, 0, TimeSpan.Zero);

    private readonly IReportRepository _repository = Substitute.For<IReportRepository>();
    private readonly ReportService _sut;

    public ReportServiceTests()
    {
        _sut = new ReportService(_repository, new FixedTimeProvider(Now));
    }

    [Fact]
    public async Task GetTopProductsAsync_ExplicitDates_ConvertsInclusiveEndToHalfOpenRange()
    {
        ReportPeriod? captured = null;
        _repository.GetTopProductsAsync(Arg.Do<ReportPeriod>(p => captured = p), 5, 2, Arg.Any<CancellationToken>())
            .Returns([new TopProduct { ProductId = 1, RevenueRank = 1 }]);

        PeriodReport<TopProduct> report = await _sut.GetTopProductsAsync(
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), top: 5, categoryId: 2);

        var expected = new ReportPeriod(
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        captured.Should().Be(expected);
        report.Period.Should().Be(expected);
        report.Items.Should().ContainSingle().Which.ProductId.Should().Be(1);
    }

    [Fact]
    public async Task GetTopProductsAsync_NoParameters_UsesTrailingTwelveMonthsAndDefaultTop()
    {
        await _sut.GetTopProductsAsync(from: null, to: null, top: null, categoryId: null);

        await _repository.Received(1).GetTopProductsAsync(
            new ReportPeriod(
                new DateTime(2025, 3, 16, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc)),
            ReportService.DefaultTop,
            null,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(ReportService.MaxTop + 1)]
    public async Task GetTopProductsAsync_TopOutOfRange_ThrowsValidation(int top)
    {
        Func<Task> act = () => _sut.GetTopProductsAsync(null, null, top, null);

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Errors.Should().ContainKey("top");
        await _repository.DidNotReceiveWithAnyArgs().GetTopProductsAsync(default!, default, default, default);
    }

    [Fact]
    public async Task GetTopProductsAsync_FromAfterTo_ThrowsValidation()
    {
        Func<Task> act = () => _sut.GetTopProductsAsync(new DateOnly(2025, 6, 2), new DateOnly(2025, 6, 1), 10, null);

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Errors.Should().ContainKey("from");
    }

    [Fact]
    public async Task GetTopProductsAsync_SameDayRange_CoversThatWholeDay()
    {
        var day = new DateOnly(2025, 6, 1);

        PeriodReport<TopProduct> report = await _sut.GetTopProductsAsync(day, day, 10, null);

        (report.Period.ToUtcExclusive - report.Period.FromUtc).Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task GetSalesByCategoryAsync_PassesRollUpFlagAndPeriod()
    {
        _repository.GetSalesByCategoryAsync(Arg.Any<ReportPeriod>(), false, Arg.Any<CancellationToken>())
            .Returns([new CategorySales { CategoryId = 4, Revenue = 10m }]);

        PeriodReport<CategorySales> report = await _sut.GetSalesByCategoryAsync(
            new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 31), rollUpToRoot: false);

        report.Items.Should().ContainSingle().Which.CategoryId.Should().Be(4);
        report.Period.ToUtcExclusive.Should().Be(new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(1999)]
    [InlineData(2101)]
    public async Task GetMonthlySalesAsync_YearOutOfRange_ThrowsValidation(int year)
    {
        Func<Task> act = () => _sut.GetMonthlySalesAsync(year);

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Errors.Should().ContainKey("year");
    }

    [Fact]
    public async Task GetMonthlySalesAsync_ValidYear_DelegatesToRepository()
    {
        _repository.GetMonthlySalesAsync(2025, Arg.Any<CancellationToken>())
            .Returns([new MonthlySales { Year = 2025, Month = 1 }]);

        IReadOnlyList<MonthlySales> result = await _sut.GetMonthlySalesAsync(2025);

        result.Should().ContainSingle().Which.Month.Should().Be(1);
    }

    [Fact]
    public async Task GetLowStockAsync_NonPositiveWarehouse_ThrowsValidation()
    {
        Func<Task> act = () => _sut.GetLowStockAsync(0);

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Errors.Should().ContainKey("warehouseId");
    }

    [Fact]
    public async Task GetLowStockAsync_NoFilter_ReturnsAllWarehouses()
    {
        _repository.GetLowStockAsync(null, Arg.Any<CancellationToken>())
            .Returns([new LowStockItem { Sku = "A", WarehouseId = 1 }, new LowStockItem { Sku = "B", WarehouseId = 2 }]);

        IReadOnlyList<LowStockItem> result = await _sut.GetLowStockAsync(null);

        result.Select(r => r.WarehouseId).Should().BeEquivalentTo([1, 2]);
    }
}
