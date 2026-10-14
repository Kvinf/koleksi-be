using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Standing;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Orders;

/// <summary>
/// Lets a buyer give up an order they have not paid for, freeing the item at once rather than
/// at the hold's own expiry.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="EscrowState.AwaitingPayment"/> can be cancelled (the domain's
/// <see cref="Order.Cancel"/> says so with <c>order.not_cancellable</c>). Once money is in escrow
/// the way out is a dispute, never a cancel.
/// </para>
/// <para>
/// A stranger gets the answer a missing order gets, so the route cannot be used to probe which
/// order ids exist. The seller is a party, and keeps the coded <c>order.forbidden</c> refusal.
/// </para>
/// <para>
/// The hold release is best effort, exactly as in the unpaid-order sweep: the order is already
/// cancelled by then, and a hold that is not released simply lapses on its own.
/// </para>
/// <para>
/// The outcome is deliberately a flat record: the endpoint runs this through the idempotency
/// store, which round-trips the result as JSON, and an <see cref="Order"/> aggregate is not
/// something to serialise.
/// </para>
/// </remarks>
public sealed partial class CancelOrderHandler(
    IOrderRepository orders,
    IListingHoldStore holds,
    AccountStandingService standing,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<CancelOrderHandler> logger)
{
    public async Task<CancelOrderOutcome> HandleAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var callerId = currentUser.RequireUserId();
        var order = await orders.FindAsync(orderId, ct).ConfigureAwait(false);

        if (order is null
            || (!string.Equals(order.BuyerId, callerId, StringComparison.Ordinal)
                && !string.Equals(order.SellerId, callerId, StringComparison.Ordinal)))
        {
            return CancelOrderOutcome.NotFound;
        }

        if (!string.Equals(order.BuyerId, callerId, StringComparison.Ordinal))
        {
            throw DomainException.For("order.forbidden", "Only the buyer can cancel an order.");
        }

        var expectedVersion = order.Version;
        order.Cancel(clock);
        await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);

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

        // Walking away from a deal both sides had agreed to (an accepted nego offer, an auction won) is a
        // broken commitment, the same as letting its time run out. A plain sale is not.
        await OrderViolations.RecordIfCommitmentAsync(standing, order, ct).ConfigureAwait(false);

        return new CancelOrderOutcome(true, order.Id);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Order {OrderId} cancelled by the buyer, but releasing the hold on listing {ListingId} failed; it lapses by itself.")]
    private static partial void LogHoldReleaseFailed(ILogger logger, string orderId, string listingId, Exception exception);
}

/// <param name="Found"><c>false</c> when the order does not exist, or the caller is not a party to it.</param>
/// <param name="OrderId">The cancelled order, when <paramref name="Found"/>.</param>
public sealed record CancelOrderOutcome(bool Found, string? OrderId)
{
    public static CancelOrderOutcome NotFound { get; } = new(false, null);
}
