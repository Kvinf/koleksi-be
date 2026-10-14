using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Sweeps;

/// <summary>
/// Runs the scheduled duties one after another and reports what they did.
/// </summary>
/// <remarks>
/// <para>
/// Triggered every few minutes by EventBridge (through <c>POST /internal/sweep</c>). The duties
/// are independent, so one failing (a bad row, a throttled table) is counted and logged and the
/// others still run; the failed one is simply still due next time. They share one
/// <see cref="SweepBudget"/>, so the whole run, not each duty, stays inside the Lambda's timeout.
/// </para>
/// <para>
/// <b>Order.</b> Auctions first, because a bidder is waiting on the result; then couriers (which can start a
/// buyer's confirmation window), escrow release and payouts, which move money state; then the winners who
/// never paid and the lapsed unpaid orders, which hand stock back and record warnings.
/// </para>
/// <para>
/// <b>Double delivery is harmless.</b> EventBridge may deliver a tick twice and a retry may follow
/// a timeout. Every duty re-checks the domain rule per item and writes with a version condition,
/// so a repeat finds nothing left to do or conflicts and skips.
/// </para>
/// </remarks>
public sealed partial class SweepOrchestrator(
    CloseDueAuctionsHandler closeAuctions,
    TrackShipmentsHandler trackShipments,
    ReleaseDueEscrowHandler releaseEscrow,
    SendPendingPayoutsHandler sendPayouts,
    ExpireAuctionPaymentsHandler expireAuctionPayments,
    CancelUnpaidOrdersHandler cancelUnpaid,
    ILogger<SweepOrchestrator> logger)
{
    public Task<SweepSummary> RunAsync(CancellationToken ct) => RunAsync(new SweepBudget(), ct);

    public async Task<SweepSummary> RunAsync(SweepBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var errors = 0;
        CloseDueAuctionsResult? closed = null;
        TrackShipmentsResult? tracked = null;
        ReleaseDueEscrowResult? released = null;
        SendPendingPayoutsResult? paidOut = null;
        ExpireAuctionPaymentsResult? lapsedWinners = null;
        CancelUnpaidOrdersResult? cancelled = null;

        try
        {
            closed = await closeAuctions.RunAsync(budget, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            errors++;
            LogDutyFailed(logger, "close-auctions", failure);
        }

        try
        {
            tracked = await trackShipments.RunAsync(budget, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            errors++;
            LogDutyFailed(logger, "track-shipments", failure);
        }

        try
        {
            released = await releaseEscrow.RunAsync(budget, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            errors++;
            LogDutyFailed(logger, "release-escrow", failure);
        }

        try
        {
            paidOut = await sendPayouts.RunAsync(budget, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            errors++;
            LogDutyFailed(logger, "send-payouts", failure);
        }

        try
        {
            lapsedWinners = await expireAuctionPayments.RunAsync(budget, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            errors++;
            LogDutyFailed(logger, "auction-payments", failure);
        }

        try
        {
            cancelled = await cancelUnpaid.RunAsync(budget, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            errors++;
            LogDutyFailed(logger, "cancel-unpaid-orders", failure);
        }

        var summary = new SweepSummary(
            ClosedAuctions: closed?.Sold ?? 0,
            EndedWithoutSale: closed?.Unsold ?? 0,
            CancelledOrders: cancelled?.Cancelled ?? 0,
            ReleasedOrders: released?.Released ?? 0,
            Errors: errors + (closed?.Errors ?? 0) + (released?.Errors ?? 0) + (cancelled?.Errors ?? 0)
                + (tracked?.Errors ?? 0) + (paidOut?.Errors ?? 0) + (lapsedWinners?.Errors ?? 0),
            DurationMs: (long)budget.Elapsed.TotalMilliseconds,
            DeliveredOrders: tracked?.Delivered ?? 0,
            PaidOutOrders: paidOut?.Sent ?? 0,
            LapsedAuctionPayments: lapsedWinners?.Lapsed ?? 0);

        LogCompleted(
            logger, summary.ClosedAuctions, summary.EndedWithoutSale, summary.CancelledOrders,
            summary.ReleasedOrders, summary.Errors, summary.DurationMs);

        return summary;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Sweep duty {Duty} failed as a whole; the other duties still ran and this one is retried next run.")]
    private static partial void LogDutyFailed(ILogger logger, string duty, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep completed: {ClosedAuctions} auctions sold, {EndedWithoutSale} without sale, {CancelledOrders} orders cancelled, {ReleasedOrders} released, {Errors} errors, {DurationMs} ms.")]
    private static partial void LogCompleted(
        ILogger logger, int closedAuctions, int endedWithoutSale, int cancelledOrders, int releasedOrders, int errors, long durationMs);
}
