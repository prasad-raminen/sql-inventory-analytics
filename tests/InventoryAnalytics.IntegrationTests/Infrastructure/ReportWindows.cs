namespace InventoryAnalytics.IntegrationTests.Infrastructure;

/// <summary>
/// Every test that aggregates orders inserts them into its OWN calendar window, far away from the
/// 2025 seed history and from orders placed "now" by usp_PlaceOrder. Reports over that window then
/// see exactly the rows the test created, regardless of test order.
/// </summary>
public static class ReportWindows
{
    public static readonly DateTime TopProductsRanking = new(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc);      // + Dec 2030 / Feb 2031 boundary rows
    public static readonly DateTime TopProductsByCategory = new(2031, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime SalesByCategory = new(2031, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime RepositoryTopProducts = new(2032, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime RepositorySalesByCategory = new(2032, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    public const int MonthlySalesYear = 2033;
    public const int RepositoryMonthlySalesYear = 2034;
}
