using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Standing;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Sweeps;

/// <summary>
/// Cancels orders that were never paid for once their payment window has elapsed.
/// </summary>
/// <remarks>
/// <para>
/// Until now an unpaid order stayed <c>AwaitingPayment</c> forever: the listing hold lapsed by
/// itself, and readers treated the order as dead (<see cref="Order.HasPaymentExpired"/>), but the
/// row never said so. This writes the truth down: <see cref="Order.Cancel"/>, version-checked, so
/// a payment that lands in the same instant wins or loses cleanly instead of both happening.
/// </para>
/// <para>
/// <b>Offers.</b> An order from an accepted offer needs no extra work here. The offer is
/// <c>CheckedOut</c> pointing at this order, and the existing reopening logic
/// (<c>OfferOrders.IsDeadAsync</c>, which treats a cancelled order as dead) hands the deal back to
/// the buyer the next time they look at it or check out. Finding the offer from the order would
/// need an index that does not exist, and duplicating the reopening rule here would fork it.
/// </para>
/// <para>
/// <b>Holds.</b> Each line's listing hold is released so the item is free at once rather than at
/// the hold's own expiry. The release is conditional on buyer and order, so it can never free a
/// hold a different buyer has since taken. Best effort: a failure only means the hold lapses by
/// itself, which is what happened before this sweep existed.
/// </para>
/// </remarks>
public sealed partial class CancelUnpaidOrdersHandler(
    IOrderRepository orders,
    IListingHoldStore holds,
    AccountStandingService standing,
    IClock clock,
    ILogger<CancelUnpaidOrdersHandler> logger)
{
    /// <summary>How long after the payment window closes an unpaid order is left before it is cancelled.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    public async Task<CancelUnpaidOrdersResult> RunAsync(SweepBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        // Cancelled a little AFTER the payment window closes, not at it. The payment session lives exactly
        // as long as the window, so a buyer who pays in its last moments (or whose notification is delayed)
        // would otherwise find the order already cancelled when the webhook lands, with the money taken
        // and no refund path. The grace is cheap insurance: the order stays unpayable for the buyer
        // either way, and the listing hold has already lapsed on its own.
        var cutoff = clock.UtcNow - Grace;
        var due = await orders.ListPaymentExpiredAsync(cutoff, budget.MaxItemsPerDuty, ct).ConfigureAwait(false);

        int cancelled = 0, skipped = 0, errors = 0;

        foreach (var order in due)
        {
            ct.ThrowIfCancellationRequested();

            if (budget.IsTimeUp)
            {
                LogTimeUp(logger, due.Count);
                break;
            }

            // The index can lag a payment. The domain rule is what decides, and the version check
            // below is what makes the decision stick.
            if (clock.UtcNow <= order.PaymentDueAt + Grace)
            {
                skipped++;
                continue;
            }

            try
            {
                var expectedVersion = order.Version;
                order.Cancel(clock);
                await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);
                cancelled++;
            }
            catch (Exception failure) when (failure is ConcurrencyConflictException or DomainException)
            {
                // Paid, or otherwise changed, between the read and the write: no longer ours.
                skipped++;
                continue;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                errors++;
                LogItemFailed(logger, order.Id, failure);
                continue;
            }

            await ReleaseHoldsAsync(order, ct).ConfigureAwait(false);

            // An accepted nego deal or an auction win that was never paid for is a broken commitment.
            await OrderViolations.RecordIfCommitmentAsync(standing, order, ct).ConfigureAwait(false);
        }

        LogSummary(logger, due.Count, cancelled, skipped, errors);
        return new CancelUnpaidOrdersResult(cancelled, skipped, errors);
    }

    private async Task ReleaseHoldsAsync(Order order, CancellationToken ct)
    {
        foreach (var line in order.Lines)
        {
            try
            {
                await holds.ReleaseAsync(line.ListingId, order.BuyerId, order.Id, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                LogHoldReleaseFailed(logger, order.Id, line.ListingId, failure);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep cancel-unpaid-orders: {Due} due, {Cancelled} cancelled, {Skipped} skipped, {Errors} failed.")]
    private static partial void LogSummary(ILogger logger, int due, int cancelled, int skipped, int errors);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Sweep cancel-unpaid-orders: could not cancel order {OrderId}; it stays due for the next run.")]
    private static partial void LogItemFailed(ILogger logger, string orderId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep cancel-unpaid-orders: time allowance used up with {Due} due; the rest wait for the next run.")]
    private static partial void LogTimeUp(ILogger logger, int due);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Order {OrderId} cancelled, but releasing the hold on listing {ListingId} failed; it lapses by itself.")]
    private static partial void LogHoldReleaseFailed(ILogger logger, string orderId, string listingId, Exception exception);
}

/// <param name="Cancelled">Orders moved to <c>Cancelled</c>.</param>
/// <param name="Skipped">Found due but already paid, cancelled, or changed by someone else.</param>
/// <param name="Errors">Failed unexpectedly; still due next run.</param>
public sealed record CancelUnpaidOrdersResult(int Cancelled, int Skipped, int Errors);
