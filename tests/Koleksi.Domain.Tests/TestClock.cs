using Koleksi.Domain.Common;

namespace Koleksi.Domain.Tests;

/// <summary>A clock the test controls, so time-dependent rules can be exercised directly.</summary>
internal sealed class TestClock(DateTimeOffset start) : IClock
{
    public static TestClock AtEpoch() => new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public DateTimeOffset UtcNow { get; private set; } = start;

    public TestClock Advance(TimeSpan by)
    {
        UtcNow += by;
        return this;
    }
}
