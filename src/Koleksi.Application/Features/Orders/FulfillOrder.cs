using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Orders;

/// <summary>Attaches tracking and marks an order shipped, on behalf of the seller.</summary>
/// <remarks>The call site <c>ship.tsx</c>'s own <c>TODO(api)</c> comment already names.</remarks>
public sealed partial class MarkOrderShippedHandler(
    IOrderRepository orders,
    IMediaStorage media,
    IShippingClaimStore claims,
    IDeliveryTracker tracker,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<MarkOrderShippedHandler> logger)
{
    /// <summary>
    /// How far ahead of UTC's today a ship date may be. A seller in Jakarta is already on
    /// tomorrow's date for the first seven hours of UTC's day, and that is not a future date to them.
    /// </summary>
    private const int ShipDateToleranceDays = 1;

    public async Task<FulfillOrderOutcome> HandleAsync(MarkOrderShippedCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sellerId = currentUser.RequireUserId();
        var order = await orders.FindAsync(command.OrderId, ct).ConfigureAwait(false);

        // A stranger gets the answer a missing order gets, so the route cannot be used to probe
        // which order ids exist. A party in the wrong role (the buyer) keeps the coded refusal.
        if (order is null || !IsParty(order, sellerId))
        {
            return FulfillOrderOutcome.NotFound;
        }

        if (!string.Equals(order.SellerId, sellerId, StringComparison.Ordinal))
        {
            throw DomainException.For("order.forbidden", "Only the seller can perform this action.");
        }

        if (command.ShippedOn > DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(ShipDateToleranceDays))
        {
            throw DomainException.For("shipment.shipped_on_future", "The shipping date cannot be in the future.");
        }

        var shipment = Shipment.Create(
            order.Courier.CourierCode, command.TrackingNumber,
            command.PackedParcelPhotoKey, command.TrackingLabelPhotoKey, command.ShippedOn);

        // The domain's own state and courier rules run first, in memory, so a refusal for those
        // reasons costs no storage requests; nothing is saved until the proofs below check out.
        var expectedVersion = order.Version;
        order.MarkShipped(shipment, sellerId, clock);

        // Both proofs must be the seller's own shipping uploads that really exist: the proof is the
        // evidence in a dispute, so it cannot point at someone else's photo or at an avatar.
        if (!await media.VerifyOwnedUploadAsync(MediaPurpose.ShippingProof, sellerId, command.PackedParcelPhotoKey, ct).ConfigureAwait(false)
            || !await media.VerifyOwnedUploadAsync(MediaPurpose.ShippingProof, sellerId, command.TrackingLabelPhotoKey, ct).ConfigureAwait(false))
        {
            throw DomainException.For(
                "shipment.proof_not_uploaded", "Upload both proof photos for this shipment before marking it shipped.");
        }

        // The same photos or waybill cannot back a second order: the proof is the evidence in a dispute, and a seller who
        // uploads one set once could otherwise "ship" every order with it. Claimed after the uploads are known to be
        // real (so a refused request claims nothing) and before the save; a retry for this same order passes.
        var clash = await claims.TryClaimAsync(
            order.Id, shipment.CourierCode, shipment.TrackingNumber,
            [shipment.PackedParcelPhotoKey, shipment.TrackingLabelPhotoKey], ct).ConfigureAwait(false);
        if (clash is { } reused)
        {
            throw reused == ShippingClaimConflict.TrackingReused
                ? DomainException.For(
                    "shipment.tracking_reused", "This tracking number is already used on another order.")
                : DomainException.For(
                    "shipment.proof_reused", "These photos were already used as proof for another order. Take new photos of this parcel.");
        }

        await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);

        // Best effort and after the save: the provider being slow or down must never undo or delay a shipment.
        // A provider that is polled (not pushed to) finds the parcel by waybill on its own at the next sweep.
        if (tracker.IsConfigured)
        {
            try
            {
                await tracker.RegisterShipmentAsync(order, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                LogRegisterFailed(logger, order.OrderNumber, failure);
            }
        }

        return FulfillOrderOutcome.Success(order);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Order {OrderNumber} is shipped, but the courier-tracking provider could not be told about it. Delivery will not be pushed for this parcel.")]
    private static partial void LogRegisterFailed(ILogger logger, string orderNumber, Exception exception);

    private static bool IsParty(Order order, string userId) =>
        string.Equals(order.BuyerId, userId, StringComparison.Ordinal)
        || string.Equals(order.SellerId, userId, StringComparison.Ordinal);
}

/// <summary>
/// Releases escrow by explicit buyer confirmation — "I received this, everything's fine".
/// </summary>
public sealed class ConfirmDeliveryHandler(IOrderRepository orders, EscrowReleaseService release, ICurrentUser currentUser)
{
    public async Task<FulfillOrderOutcome> HandleAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var buyerId = currentUser.RequireUserId();
        var order = await orders.FindAsync(orderId, ct).ConfigureAwait(false);

        // Same coarse answer for "no such order" and "not yours" — see MarkOrderShippedHandler.
        if (order is null
            || (!string.Equals(order.BuyerId, buyerId, StringComparison.Ordinal)
                && !string.Equals(order.SellerId, buyerId, StringComparison.Ordinal)))
        {
            return FulfillOrderOutcome.NotFound;
        }

        if (!string.Equals(order.BuyerId, buyerId, StringComparison.Ordinal))
        {
            throw DomainException.For("order.forbidden", "Only the buyer can confirm receipt.");
        }

        // Releases the escrow and records the payout the seller is owed, in one write.
        await release.ReleaseAsync(order, confirmedByBuyer: true, ct).ConfigureAwait(false);

        return FulfillOrderOutcome.Success(order);
    }
}

/// <summary>Opens a dispute on behalf of the buyer, freezing release while support reviews it.</summary>
public sealed class OpenOrderDisputeHandler(IOrderRepository orders, ICurrentUser currentUser, IClock clock)
{
    public async Task<FulfillOrderOutcome> HandleAsync(string orderId, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var buyerId = currentUser.RequireUserId();
        var order = await orders.FindAsync(orderId, ct).ConfigureAwait(false);

        // Same coarse answer for "no such order" and "not yours" — see MarkOrderShippedHandler.
        // The seller is a party, so they fall through to the domain's own order.forbidden.
        if (order is null
            || (!string.Equals(order.BuyerId, buyerId, StringComparison.Ordinal)
                && !string.Equals(order.SellerId, buyerId, StringComparison.Ordinal)))
        {
            return FulfillOrderOutcome.NotFound;
        }

        // Only the buyer may open one; the party check inside OpenDispute gives the seller their own refusal first.
        if (string.Equals(order.BuyerId, buyerId, StringComparison.Ordinal))
        {
            order.EnsureDisputeWindowOpen(clock);
        }

        var expectedVersion = order.Version;
        order.OpenDispute(reason, buyerId, clock);
        await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);

        return FulfillOrderOutcome.Success(order);
    }
}

public sealed record MarkOrderShippedCommand(
    string OrderId,
    string TrackingNumber,
    string PackedParcelPhotoKey,
    string TrackingLabelPhotoKey,
    DateOnly ShippedOn);

/// <param name="Found"><c>false</c> when the order does not exist, or the caller is not a party to it.</param>
/// <param name="EscrowState">The order's state after the action.</param>
public sealed record FulfillOrderOutcome(bool Found, EscrowState? EscrowState)
{
    public static FulfillOrderOutcome NotFound { get; } = new(false, null);

    public static FulfillOrderOutcome Success(Order order) => new(true, order.EscrowState);
}
