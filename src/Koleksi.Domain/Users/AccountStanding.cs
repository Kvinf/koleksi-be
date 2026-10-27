using Koleksi.Domain.Common;

namespace Koleksi.Domain.Users;

/// <summary>What an account did wrong. Each one is a broken commitment, not a mere change of mind.</summary>
public enum ViolationType
{
    /// <summary>
    /// Won (or bought out) an auction and did not pay inside the thirty minutes, whether by letting the
    /// time run out or by cancelling the order.
    /// </summary>
    AuctionWinNotPaid = 0,

    /// <summary>
    /// Had a nego offer accepted, started the purchase, and did not pay inside the deadline, whether by
    /// letting the time run out or by cancelling the order.
    /// </summary>
    AcceptedOfferNotPaid = 1,
}

/// <summary>One recorded violation: what, which deal, and when.</summary>
/// <param name="Reference">
/// The deal it belongs to (the auction or the offer), so one broken deal is one warning however many
/// ways it is detected.
/// </param>
public sealed record Violation(ViolationType Type, string Reference, DateTimeOffset At);

/// <summary>What recording a violation did.</summary>
public enum StandingOutcome
{
    /// <summary>A warning was added; the account is still in good standing.</summary>
    Warned = 0,

    /// <summary>This was the warning that reached the limit: the account is now banned.</summary>
    Banned = 1,

    /// <summary>The account was already banned; nothing changed.</summary>
    AlreadyBanned = 2,

    /// <summary>This very deal had already been counted; nothing changed.</summary>
    Duplicate = 3,
}

/// <summary>
/// An account's record for keeping its word: warnings for broken commitments, a ban at three, and one
/// warning forgiven for every clean month.
/// </summary>
/// <remarks>
/// <para>
/// <b>What earns a warning.</b> Only a commitment that was agreed and then dropped (see
/// <see cref="ViolationType"/>): an auction won and not paid for in thirty minutes, an accepted nego
/// deal not paid for in its fifteen. A plain sale walked away from is not a violation: the units simply
/// go back on the shelf when the reservation lapses.
/// </para>
/// <para>
/// <b>Forgiveness is a clock, not a job.</b> A warning counts down by itself: for every thirty days with
/// no new warning, one is forgiven. That is computed when the record is read
/// (<see cref="WarningsAt"/>), from the time of the last warning, so there is no sweep to run and no
/// way for the number to drift. A new warning starts from the forgiven count, not the stored one.
/// </para>
/// <para>
/// <b>A ban is not forgiven.</b> Three warnings at once ban the account and nothing in this type
/// lifts it; only support can.
/// </para>
/// </remarks>
public sealed class AccountStanding
{
    /// <summary>Warnings at which an account is banned.</summary>
    public const int WarningsToBan = 3;

    /// <summary>A clean stretch this long forgives one warning.</summary>
    public static readonly TimeSpan CleanPeriod = TimeSpan.FromDays(30);

    /// <summary>How many past violations are kept on the record (and used to avoid counting a deal twice).</summary>
    public const int MaxRememberedViolations = 20;

    private readonly List<Violation> _recent = [];

    private AccountStanding(string userId)
    {
        UserId = userId;
    }

    public string UserId { get; }

    /// <summary>The count as of <see cref="LastWarningAt"/>, before any forgiveness. Read through <see cref="WarningsAt"/>.</summary>
    public int StoredWarnings { get; private set; }

    public DateTimeOffset? LastWarningAt { get; private set; }

    public DateTimeOffset? BannedAt { get; private set; }

    /// <summary>Optimistic-concurrency token.</summary>
    public long Version { get; private set; }

    /// <summary>The most recent violations, oldest first.</summary>
    public IReadOnlyList<Violation> Recent => _recent;

    public bool IsBanned => BannedAt is not null;

    /// <summary>A record with nothing on it, for an account that has never been warned.</summary>
    public static AccountStanding Clean(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new AccountStanding(userId);
    }

    /// <summary>Reconstructs a stored record. Only the persistence layer calls this.</summary>
    public static AccountStanding Rehydrate(
        string userId,
        int storedWarnings,
        DateTimeOffset? lastWarningAt,
        DateTimeOffset? bannedAt,
        IEnumerable<Violation> recent,
        long version)
    {
        var standing = new AccountStanding(userId)
        {
            StoredWarnings = storedWarnings,
            LastWarningAt = lastWarningAt,
            BannedAt = bannedAt,
            Version = version,
        };
        standing._recent.AddRange(recent);
        return standing;
    }

    /// <summary>
    /// The warnings the account carries at <paramref name="now"/>: the stored count less one for every
    /// full clean month since the last warning. A banned account keeps what it had.
    /// </summary>
    public int WarningsAt(DateTimeOffset now)
    {
        if (IsBanned)
        {
            return StoredWarnings;
        }

        if (StoredWarnings <= 0 || LastWarningAt is not { } last)
        {
            return 0;
        }

        return Math.Max(0, StoredWarnings - CleanPeriodsSince(last, now));
    }

    /// <summary>
    /// When the next warning will be forgiven, or <c>null</c> when there is nothing to forgive (no warnings,
    /// or banned).
    /// </summary>
    public DateTimeOffset? NextReductionAt(DateTimeOffset now)
    {
        if (IsBanned || WarningsAt(now) == 0 || LastWarningAt is not { } last)
        {
            return null;
        }

        return last + CleanPeriod * (CleanPeriodsSince(last, now) + 1);
    }

    /// <summary>Counts a broken commitment against the account.</summary>
    /// <returns>What happened; <see cref="StandingOutcome.Banned"/> when this warning was the third.</returns>
    public StandingOutcome Record(ViolationType type, string reference, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(clock);

        if (IsBanned)
        {
            return StandingOutcome.AlreadyBanned;
        }

        if (_recent.Exists(v => v.Type == type && string.Equals(v.Reference, reference, StringComparison.Ordinal)))
        {
            return StandingOutcome.Duplicate;
        }

        var now = clock.UtcNow;
        StoredWarnings = WarningsAt(now) + 1;
        LastWarningAt = now;

        _recent.Add(new Violation(type, reference, now));
        if (_recent.Count > MaxRememberedViolations)
        {
            _recent.RemoveRange(0, _recent.Count - MaxRememberedViolations);
        }

        Version++;

        if (StoredWarnings >= WarningsToBan)
        {
            BannedAt = now;
            return StandingOutcome.Banned;
        }

        return StandingOutcome.Warned;
    }

    private static int CleanPeriodsSince(DateTimeOffset last, DateTimeOffset now) =>
        now <= last ? 0 : (int)Math.Floor((now - last) / CleanPeriod);
}
