using Koleksi.Domain.Common;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// A clock a test can set.
/// </summary>
/// <remarks>
/// Auction endings, anti-sniping, offer expiry, and escrow auto-release are all
/// time-driven. Against the real clock, an assertion about "inside the last two minutes"
/// is a race with the test runner.
/// </remarks>
public sealed class MutableClock : IClock
{
    /// <summary>
    /// A fixed instant every test starts from.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>DateTimeOffset.UtcNow</c> at construction: two runs of the same
    /// suite should exercise the same instants, and a test that only fails on a leap day
    /// is worse than no test.
    /// </remarks>
    public static readonly DateTimeOffset Origin = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; } = Origin;

    public void Advance(TimeSpan by) => UtcNow += by;
}
