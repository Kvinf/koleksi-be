using System.Diagnostics;

namespace Koleksi.Application.Features.Sweeps;

/// <summary>
/// How much one sweep run may do: a number of items per duty, and a wall-clock allowance.
/// </summary>
/// <remarks>
/// <para>
/// The sweep runs inside the API Lambda (25 s timeout), triggered every few minutes. A backlog
/// must therefore be worked off in slices: each duty reads at most <see cref="MaxItemsPerDuty"/>
/// due items, and stops <em>starting</em> new ones once <see cref="IsTimeUp"/>. What is left is
/// simply still due at the next tick, so nothing is lost by stopping early, and an item already in
/// flight still has the remainder of the Lambda's timeout to finish.
/// </para>
/// <para>
/// The allowance is real elapsed time, not <c>IClock</c>: the clock is what the domain rules judge
/// "due" by, and a test that moves it forward must not also exhaust the budget.
/// </para>
/// </remarks>
public sealed class SweepBudget
{
    /// <summary>Items read and handled per duty per run.</summary>
    public const int DefaultMaxItemsPerDuty = 50;

    /// <summary>Leaves about five seconds of the Lambda's 25 for the item in flight and the response.</summary>
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(20);

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TimeSpan _timeLimit;

    public SweepBudget(int maxItemsPerDuty = DefaultMaxItemsPerDuty, TimeSpan? timeLimit = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItemsPerDuty);

        MaxItemsPerDuty = maxItemsPerDuty;
        _timeLimit = timeLimit ?? DefaultTimeLimit;
    }

    public int MaxItemsPerDuty { get; }

    /// <summary>Whether no further item should be started.</summary>
    public bool IsTimeUp => _stopwatch.Elapsed >= _timeLimit;

    public TimeSpan Elapsed => _stopwatch.Elapsed;
}

/// <summary>What one sweep run did, as the trigger sees it.</summary>
/// <param name="ClosedAuctions">Auctions closed with a winner (<c>Ended</c>), awaiting that winner's payment.</param>
/// <param name="EndedWithoutSale">Auctions closed with no bids, or below their reserve.</param>
/// <param name="CancelledOrders">Unpaid orders cancelled because their payment window elapsed.</param>
/// <param name="ReleasedOrders">Delivered orders whose escrow released because the buyer's window closed.</param>
/// <param name="Errors">Items (or whole duties) that failed and will be retried by a later run.</param>
/// <param name="DurationMs">Elapsed wall-clock time.</param>
public sealed record SweepSummary(
    int ClosedAuctions,
    int EndedWithoutSale,
    int CancelledOrders,
    int ReleasedOrders,
    int Errors,
    long DurationMs,
    int DeliveredOrders = 0,
    int PaidOutOrders = 0,
    int LapsedAuctionPayments = 0);
