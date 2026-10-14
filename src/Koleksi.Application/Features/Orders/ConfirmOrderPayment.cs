using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Orders;

/// <summary>
/// The payment webhook's call site: verifies the gateway's signature, then moves the
/// order's escrow from <c>AwaitingPayment</c> to <c>Paid</c> and marks its listing sold.
/// </summary>
/// <remarks>
/// <para>
/// Reachable only from <c>POST /api/payments/webhook</c>, never from an authenticated
/// client request — there is no <see cref="ICurrentUser"/> dependency here at all. The
/// only thing that authorizes this call is
/// <see cref="IPaymentGateway.VerifyAndParseWebhook"/> having accepted the raw body's HMAC
/// signature.
/// </para>
/// <para>
/// Deduped twice over, deliberately: <see cref="IIdempotencyStore"/> claims the
/// notification's own <c>EventId</c> — gateways retry deliveries — and
/// <c>Order.ConfirmPayment</c> is itself a no-op on an already-paid order, so a claim that
/// somehow lands twice still cannot double-release a listing.
/// </para>
/// <para>
/// <b>A payment is never dropped.</b> The gateway has taken the money by the time this runs, so a
/// settlement that cannot be applied (the order was cancelled first, the amount is wrong, the item
/// sold to someone else) is written to <see cref="IPaymentOrphanStore"/>, logged at error level,
/// and acknowledged with <c>200</c>: a retry of the same notification would fail the same way for
/// ever. Failures that a retry can fix (a lost write race, storage down) are the opposite: they
/// throw, the claim is released, and the gateway delivers again.
/// </para>
/// <para>
/// <b>No half state.</b> The order is saved Paid before its listings are marked sold. If the second
/// step fails after retries the exception escapes, the delivery is retried, and the repeat finds
/// the order already Paid (a no-op) and finishes the listing, so the pair converges.
/// </para>
/// </remarks>
public sealed partial class ConfirmOrderPaymentHandler(
    IOrderRepository orders,
    IListingRepository listings,
    IListingHoldStore holds,
    IListingSearchIndex searchIndex,
    IPaymentGateway payments,
    IPaymentOrphanStore orphans,
    IIdempotencyStore idempotency,
    IClock clock,
    ILogger<ConfirmOrderPaymentHandler> logger)
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Payment for order {OrderNumber} ({AmountRupiah} rupiah, processor reference {ProcessorReference}) could not be applied: {Reason}. The buyer was charged; a refund is needed. It is recorded as an orphaned payment.")]
    private static partial void LogOrphanedPayment(
        ILogger logger, string orderNumber, long amountRupiah, string processorReference, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Order {OrderNumber} was paid but listing {ListingId} was no longer purchasable. The buyer needs a refund.")]
    private static partial void LogPaidForSoldItem(ILogger logger, string orderNumber, string listingId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Order {OrderNumber} is paid and listing {ListingId} is sold, but removing it from browse failed.")]
    private static partial void LogBrowseRemovalFailed(ILogger logger, string orderNumber, string listingId, Exception exception);

    private static readonly TimeSpan ClaimRetention = TimeSpan.FromDays(1);

    /// <summary>How often a version conflict on the order or its listing is re-read and retried before giving up.</summary>
    private const int MaxAttempts = 4;

    public async Task<ConfirmOrderPaymentResult> HandleAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(headers);

        var notification = payments.VerifyAndParseWebhook(rawBody, headers);
        if (notification is null)
        {
            return ConfirmOrderPaymentResult.Unverified;
        }

        return await idempotency.RunOnceAsync(
            "payment.webhook",
            notification.EventId,
            ClaimRetention,
            () => ApplyAsync(notification, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a notification the caller has already trusted, skipping the signature check. For the
    /// development host's payment simulator only (<c>/internal/dev/pay-order</c>, mapped in Development
    /// alone): a laptop has no payment gateway to send a signed webhook.
    /// </summary>
    public Task<ConfirmOrderPaymentResult> ApplyTrustedAsync(PaymentNotification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return idempotency.RunOnceAsync("payment.webhook", notification.EventId, ClaimRetention, () => ApplyAsync(notification, ct), ct);
    }

    private async Task<ConfirmOrderPaymentResult> ApplyAsync(PaymentNotification notification, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var order = await orders.FindByOrderNumberAsync(notification.ReferenceId, ct).ConfigureAwait(false);
            if (order is null)
            {
                return ConfirmOrderPaymentResult.OrderNotFound;
            }

            if (notification.Status != PaymentStatus.Settled)
            {
                // Pending, failed, or expired notifications carry nothing for escrow to act
                // on — the order simply stays AwaitingPayment until either a Settled
                // notification arrives or Order.HasPaymentExpired catches it.
                return ConfirmOrderPaymentResult.Acknowledged;
            }

            var firstSettlement = order.EscrowState == EscrowState.AwaitingPayment;
            var expectedVersion = order.Version;
            var stateBefore = order.EscrowState;

            try
            {
                order.ConfirmPayment(notification.SettledAmount, clock);
            }
            catch (DomainException failure) when (failure.Code is "order.not_payable" or "order.amount_mismatch")
            {
                // Money has been taken for an order that cannot accept it. Not a retryable error.
                var reason = failure.Code == "order.amount_mismatch"
                    ? "amount_mismatch"
                    : "order_" + stateBefore.ToString().ToLowerInvariant();
                await RecordOrphanAsync(notification, order, reason, ct).ConfigureAwait(false);
                return ConfirmOrderPaymentResult.Acknowledged;
            }

            if (order.Version != expectedVersion)
            {
                try
                {
                    await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);
                }
                catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
                {
                    // Something else wrote the order first (the cancel sweep, a repeat delivery). Read
                    // it again and decide on what is really there.
                    continue;
                }
            }

            foreach (var line in order.Lines)
            {
                await MarkListingSoldAsync(order, line, firstSettlement, notification, ct).ConfigureAwait(false);
            }

            return ConfirmOrderPaymentResult.Acknowledged;
        }

        // Unreachable: the last attempt either returns or throws.
        throw new ConcurrencyConflictException($"Payment for order '{notification.ReferenceId}' could not be applied.");
    }

    /// <summary>
    /// Sells the line's listing, retrying a lost write race. Payment is authoritative, so an Archived
    /// listing is sold too (<see cref="Listing.MarkSold"/>).
    /// </summary>
    private async Task MarkListingSoldAsync(
        Order order, OrderLine line, bool firstSettlement, PaymentNotification notification, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var listing = await listings.FindAsync(line.ListingId, ct).ConfigureAwait(false);
            if (listing is null)
            {
                return;
            }

            // The order's reserved units become sold ones. A repeat delivery counts the same units again.
            // If the hold had lapsed and another buyer took the stock, fewer are committed than were paid
            // for: that is the "paid for something gone" case, recorded where a person will find it.
            var committed = await holds.CommitSaleAsync(
                listing.Id, listing.Stock, order.BuyerId, order.Id, line.Quantity, ct).ConfigureAwait(false);
            if (committed < line.Quantity && firstSettlement)
            {
                LogPaidForSoldItem(logger, order.OrderNumber, line.ListingId);
                await RecordOrphanAsync(notification, order, "stock_unavailable", ct).ConfigureAwait(false);
            }

            // Only when the last unit is gone does the listing itself become Sold; while units remain it
            // stays on sale (and in browse) for the next buyer.
            var usage = await holds.GetAvailabilityAsync(listing.Id, listing.Stock, ct).ConfigureAwait(false);
            if (!usage.SoldOut)
            {
                return;
            }

            try
            {
                var expectedListingVersion = listing.Version;
                listing.MarkSold(clock);
                await listings.UpdateAsync(listing, expectedListingVersion, ct).ConfigureAwait(false);
            }
            catch (DomainException failure) when (failure.Code == "listing.not_purchasable")
            {
                // On a repeat delivery this is just the first run having already sold it. On the first
                // one it is real: money has been taken for an item that is gone (sold to someone else,
                // or suspended). Checkout holds make it rare, but there is no refund path yet, so the
                // payment is recorded where a person will find it.
                if (firstSettlement)
                {
                    LogPaidForSoldItem(logger, order.OrderNumber, line.ListingId);
                    await RecordOrphanAsync(notification, order, "listing_unavailable", ct).ConfigureAwait(false);
                }

                if (listing.Status == ListingStatus.Sold)
                {
                    await RemoveFromBrowseAsync(order, line.ListingId, ct).ConfigureAwait(false);
                }

                return;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                // A seller's edit or a view-count transaction landed in between. Read it again.
                continue;
            }

            await RemoveFromBrowseAsync(order, line.ListingId, ct).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// A sold item leaves browse and suggestions at the moment of sale (D-04). Best effort: the sale is
    /// already true, and a browse row that lingers is filtered out by status on read.
    /// </summary>
    private async Task RemoveFromBrowseAsync(Order order, string listingId, CancellationToken ct)
    {
        try
        {
            await searchIndex.RemoveAsync(listingId, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogBrowseRemovalFailed(logger, order.OrderNumber, listingId, failure);
        }
    }

    private async Task RecordOrphanAsync(
        PaymentNotification notification, Order order, string reason, CancellationToken ct)
    {
        LogOrphanedPayment(
            logger, order.OrderNumber, notification.SettledAmount.Rupiah, notification.ProcessorReference, reason);

        await orphans.RecordAsync(
            new PaymentOrphan(
                order.OrderNumber,
                order.Id,
                notification.SettledAmount,
                notification.ProcessorReference,
                notification.EventId,
                reason,
                clock.UtcNow),
            ct).ConfigureAwait(false);
    }
}

/// <param name="Verified">
/// <c>false</c> when the signature did not check out — the endpoint answers <c>401</c>
/// without naming why, the same coarse treatment every other verification failure in this
/// product gets.
/// </param>
/// <param name="OrderFound">
/// <c>false</c> when the notification's reference does not match any order. Acknowledged
/// with <c>200</c> regardless — see the endpoint — so the gateway does not retry a
/// notification that will never resolve to anything.
/// </param>
public sealed record ConfirmOrderPaymentResult(bool Verified, bool OrderFound)
{
    public static ConfirmOrderPaymentResult Unverified { get; } = new(false, false);

    public static ConfirmOrderPaymentResult OrderNotFound { get; } = new(true, false);

    public static ConfirmOrderPaymentResult Acknowledged { get; } = new(true, true);
}
