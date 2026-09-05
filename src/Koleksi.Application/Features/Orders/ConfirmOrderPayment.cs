using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;

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
/// </remarks>
public sealed class ConfirmOrderPaymentHandler(
    IOrderRepository orders,
    IListingRepository listings,
    IPaymentGateway payments,
    IIdempotencyStore idempotency,
    IClock clock)
{
    private static readonly TimeSpan ClaimRetention = TimeSpan.FromDays(1);

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

    private async Task<ConfirmOrderPaymentResult> ApplyAsync(PaymentNotification notification, CancellationToken ct)
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

        var expectedVersion = order.Version;
        order.ConfirmPayment(notification.SettledAmount, clock);
        await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);

        foreach (var line in order.Lines)
        {
            var listing = await listings.FindAsync(line.ListingId, ct).ConfigureAwait(false);
            if (listing is null)
            {
                continue;
            }

            // MarkSold throws listing.not_purchasable if it is not Active — reached when
            // a second buyer's order for the same one-of-one item settles after the
            // first, or on a webhook retry after this already ran. Neither is this
            // handler's problem to solve; both are swallowed so the payment itself is
            // still acknowledged, which is what stops the gateway from retrying forever.
            try
            {
                var expectedListingVersion = listing.Version;
                listing.MarkSold(clock);
                await listings.UpdateAsync(listing, expectedListingVersion, ct).ConfigureAwait(false);
            }
            catch (DomainException failure) when (failure.Code == "listing.not_purchasable")
            {
            }
        }

        return ConfirmOrderPaymentResult.Acknowledged;
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
