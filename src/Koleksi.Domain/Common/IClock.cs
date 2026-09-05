namespace Koleksi.Domain.Common;

/// <summary>
/// Supplies the current time.
/// </summary>
/// <remarks>
/// Auction endings, offer expiry, and escrow auto-release are all time-driven, and
/// none of them can be tested against a real clock. Domain and application code take
/// time from this interface; only the composition root ever reads
/// <see cref="DateTimeOffset.UtcNow"/>.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant, always in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The production clock.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
