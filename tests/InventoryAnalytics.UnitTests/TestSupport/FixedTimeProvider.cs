namespace InventoryAnalytics.UnitTests.TestSupport;

/// <summary>A <see cref="TimeProvider"/> frozen at a known instant, so default report periods are deterministic.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
