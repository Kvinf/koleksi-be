using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Standing;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Sweeps;

/// <summary>
/// Asks the courier about shipped parcels and moves a delivered one to <c>Delivered</c>, which starts the
/// buyer's two-day confirmation window.
/// </summary>
/// <remarks>
/// <para>
/// The domain decides what a report means (<see cref="Order.MarkDelivered"/> and friends); this only
/// supplies the schedule and the answer. Each parcel is asked about every
/// <see cref="Order.TrackingPollInterval"/>, not on every sweep, so a courier is not hammered.
/// </para>
/// <para>
/// <b>The clock starts when we learn of the delivery, not when the courier says it happened.</b> A courier
/// that reports a delivery three days late must not leave the buyer with no confirmation window at all.
/// </para>
/// <para>
/// With no provider configured the duty does nothing (and says so once per run): parcels then wait for the
/// buyer's own confirmation, which is always available from the moment an order ships.
/// </para>
/// </remarks>
public sealed partial class TrackShipmentsHandler(
    IOrderRepository orders,
    IDeliveryTracker tracker,
    IClock clock,
    ILogger<TrackShipmentsHandler> logger)
{
    public async Task<TrackShipmentsResult> RunAsync(SweepBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        if (!tracker.CanPoll)
        {
            LogNoProvider(logger);
            return new TrackShipmentsResult(0, 0, 0, 0);
        }

        var due = await orders.ListDueForTrackingAsync(clock.UtcNow, budget.MaxItemsPerDuty, ct).ConfigureAwait(false);

        int delivered = 0, checkedCount = 0, skipped = 0, errors = 0;

        foreach (var order in due)
        {
            ct.ThrowIfCancellationRequested();

            if (budget.IsTimeUp)
            {
                break;
            }

            if (order.EscrowState is not (EscrowState.Shipped or EscrowState.InTransit) || order.Shipment is null)
            {
                skipped++;
                continue;
            }

            try
            {
                var report = await tracker.CheckAsync(order, ct).ConfigureAwait(false);

                var expectedVersion = order.Version;
                if (report.State == DeliveryState.Delivered)
                {
                    DeliveryProgress.Apply(order, report.State, clock);
                    delivered++;
                }
                else
                {
                    DeliveryProgress.Apply(order, report.State, clock);
                    order.RecordTrackingCheck(clock);
                    checkedCount++;
                }

                await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is ConcurrencyConflictException or DomainException)
            {
                // Confirmed or disputed by the buyer while the courier was being asked. No longer ours.
                skipped++;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                errors++;
                LogItemFailed(logger, order.Id, failure);
            }
        }

        LogSummary(logger, due.Count, delivered, checkedCount, skipped, errors);
        return new TrackShipmentsResult(delivered, checkedCount, skipped, errors);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep track-shipments: no courier-tracking provider is configured; parcels are released only when the buyer confirms receipt.")]
    private static partial void LogNoProvider(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep track-shipments: {Due} due, {Delivered} delivered, {Checked} still on the way, {Skipped} skipped, {Errors} failed.")]
    private static partial void LogSummary(ILogger logger, int due, int delivered, int @checked, int skipped, int errors);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Sweep track-shipments: could not check order {OrderId}; it stays due for the next run.")]
    private static partial void LogItemFailed(ILogger logger, string orderId, Exception exception);
}

/// <param name="Delivered">Parcels the courier reported delivered.</param>
/// <param name="StillMoving">Parcels asked about and not delivered yet.</param>
public sealed record TrackShipmentsResult(int Delivered, int StillMoving, int Skipped, int Errors);

/// <summary>Sends the payouts that are owed and waiting: the first try, a retry, or one held for a missing bank account.</summary>
public sealed partial class SendPendingPayoutsHandler(
    IPayoutRepository payouts,
    PayoutSender sender,
    IClock clock,
    ILogger<SendPendingPayoutsHandler> logger)
{
    public async Task<SendPendingPayoutsResult> RunAsync(SweepBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var due = await payouts.ListDueAsync(clock.UtcNow, budget.MaxItemsPerDuty, ct).ConfigureAwait(false);

        int sent = 0, waiting = 0, errors = 0;

        foreach (var payout in due)
        {
            ct.ThrowIfCancellationRequested();

            if (budget.IsTimeUp)
            {
                break;
            }

            try
            {
                if (await sender.TrySendAsync(payout, ct).ConfigureAwait(false))
                {
                    sent++;
                }
                else
                {
                    waiting++;
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                errors++;
                LogItemFailed(logger, payout.OrderNumber, failure);
            }
        }

        LogSummary(logger, due.Count, sent, waiting, errors);
        return new SendPendingPayoutsResult(sent, waiting, errors);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep send-payouts: {Due} due, {Sent} sent, {Waiting} still waiting, {Errors} failed.")]
    private static partial void LogSummary(ILogger logger, int due, int sent, int waiting, int errors);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Sweep send-payouts: could not process the payout for order {OrderNumber}; it stays due.")]
    private static partial void LogItemFailed(ILogger logger, string orderNumber, Exception exception);
}

public sealed record SendPendingPayoutsResult(int Sent, int Waiting, int Errors);

/// <summary>
/// Gives a warning to an auction winner who never paid inside their thirty minutes, and hands the lot back
/// to the seller.
/// </summary>
/// <remarks>
/// <para>
/// The unpaid-order sweep already warns a winner who opened checkout and let the order lapse. This is for
/// the winner who never opened it at all: the auction has no order to find, so it is found through its own
/// due entry (see <c>Auction.PaymentDueAt</c>). Both use the same deal reference (<c>auction:{listing}</c>),
/// so a winner who did both is warned once.
/// </para>
/// <para>
/// Judged a grace period after the deadline (the same ten minutes the order sweep gives), so a payment
/// notification that is a little late still wins. If the listing is already sold, the winner paid and
/// nothing is recorded. Once handled the auction leaves the sweep, and its lot is the seller's to relist.
/// </para>
/// </remarks>
public sealed partial class ExpireAuctionPaymentsHandler(
    IAuctionRepository auctions,
    IListingRepository listings,
    AccountStandingService standing,
    IClock clock,
    ILogger<ExpireAuctionPaymentsHandler> logger)
{
    public async Task<ExpireAuctionPaymentsResult> RunAsync(SweepBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var now = clock.UtcNow;
        var due = await auctions
            .ListDueForWinnerPaymentAsync(now - CancelUnpaidOrdersHandler.Grace, budget.MaxItemsPerDuty, ct)
            .ConfigureAwait(false);

        int lapsed = 0, paid = 0, skipped = 0, errors = 0;

        foreach (var candidate in due)
        {
            ct.ThrowIfCancellationRequested();

            if (budget.IsTimeUp)
            {
                break;
            }

            try
            {
                // The index can lag; the auction itself is what decides.
                var auction = await auctions.FindAsync(candidate.ListingId, ct).ConfigureAwait(false);
                if (auction is null
                    || auction.Status != AuctionStatus.Ended
                    || auction.WinnerPaymentResolvedAt is not null
                    || auction.PaymentDueAt is not { } dueAt
                    || now <= dueAt + CancelUnpaidOrdersHandler.Grace)
                {
                    skipped++;
                    continue;
                }

                var listing = await listings.FindAsync(auction.ListingId, ct).ConfigureAwait(false);
                var winnerPaid = listing is { Status: ListingStatus.Sold };

                if (!winnerPaid && auction.LeadingBidderId is { } winner)
                {
                    await standing
                        .RecordViolationAsync(winner, ViolationType.AuctionWinNotPaid, $"auction:{auction.ListingId}", ct)
                        .ConfigureAwait(false);
                    lapsed++;
                }
                else
                {
                    paid++;
                }

                var expectedVersion = auction.Version;
                auction.ResolveWinnerPayment(clock);
                await auctions.SaveBidAsync(auction, [], expectedVersion, ct).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException)
            {
                // A payment or another sweep got there first; whatever is still due is found again.
                skipped++;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                errors++;
                LogItemFailed(logger, candidate.ListingId, failure);
            }
        }

        LogSummary(logger, due.Count, lapsed, paid, skipped, errors);
        return new ExpireAuctionPaymentsResult(lapsed, paid, skipped, errors);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep auction-payments: {Due} due, {Lapsed} winners warned, {Paid} had paid, {Skipped} skipped, {Errors} failed.")]
    private static partial void LogSummary(ILogger logger, int due, int lapsed, int paid, int skipped, int errors);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Sweep auction-payments: could not settle auction {ListingId}; it stays due for the next run.")]
    private static partial void LogItemFailed(ILogger logger, string listingId, Exception exception);
}

public sealed record ExpireAuctionPaymentsResult(int Lapsed, int Paid, int Skipped, int Errors);
