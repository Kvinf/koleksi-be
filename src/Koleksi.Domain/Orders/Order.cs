using Koleksi.Domain.Common;

namespace Koleksi.Domain.Orders;

/// <summary>
/// One buyer's purchase from one seller, and the escrow that holds the money until
/// the buyer has what they paid for.
/// </summary>
/// <remarks>
/// <para>
/// A checkout containing items from three shops produces three orders sharing one
/// payment. The design already splits checkout by shop — each with its own courier
/// choice and its own subtotal — because shipping, tracking, disputes and payouts are
/// all per-seller. Modelling an order as spanning sellers would make every one of those
/// flows ambiguous.
/// </para>
/// <para>
/// <b>Money never moves from a client request.</b> <see cref="ConfirmPayment"/> is
/// reachable only from the verified gateway webhook, and <see cref="Release"/> only from
/// buyer confirmation or the auto-release sweep. No endpoint lets a caller name an
/// amount or a destination.
/// </para>
/// </remarks>
public sealed class Order
{
    /// <summary>
    /// How long after delivery the buyer has to confirm or dispute before funds
    /// release automatically.
    /// </summary>
    /// <remarks>
    /// Without a deadline, an inattentive buyer strands a seller's money indefinitely.
    /// Three days is long enough to open and inspect a collectible, short enough that
    /// sellers can plan around it, and it matches what the order timeline tells the buyer.
    /// </remarks>
    public static readonly TimeSpan AutoReleaseAfterDelivery = TimeSpan.FromDays(3);

    /// <summary>How long an unpaid order holds its stock before auto-cancelling.</summary>
    /// <remarks>Mirrors the 15-minute QRIS countdown shown at checkout stage 2.</remarks>
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(15);

    private readonly List<OrderLine> _lines = [];

    private Order(
        string id,
        string orderNumber,
        string buyerId,
        string sellerId,
        ShippingAddress shipTo,
        CourierSelection courier,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrderNumber = orderNumber;
        BuyerId = buyerId;
        SellerId = sellerId;
        ShipTo = shipTo;
        Courier = courier;
        CreatedAt = createdAt;
        EscrowState = EscrowState.AwaitingPayment;
    }

    public string Id { get; }

    /// <summary>Human-facing reference shown on receipts, e.g. <c>KLK-8F2A41</c>.</summary>
    public string OrderNumber { get; }

    public string BuyerId { get; }
    public string SellerId { get; }

    /// <summary>
    /// The delivery address as it stood at checkout.
    /// </summary>
    /// <remarks>
    /// Copied, not referenced. A buyer editing their address book months later must not
    /// retroactively change where a past parcel was sent — that record is evidence in a
    /// dispute.
    /// </remarks>
    public ShippingAddress ShipTo { get; }

    /// <summary>The courier and service the buyer chose. Read-only to the seller afterwards.</summary>
    public CourierSelection Courier { get; }

    public IReadOnlyList<OrderLine> Lines => _lines;
    public EscrowState EscrowState { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? PaidAt { get; private set; }
    public DateTimeOffset? ShippedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }

    /// <summary>Tracking details, present from <see cref="EscrowState.Shipped"/> onwards.</summary>
    public Shipment? Shipment { get; private set; }

    /// <summary>The open or resolved dispute, if the buyer raised one.</summary>
    public Dispute? Dispute { get; private set; }

    /// <summary>Optimistic-concurrency token.</summary>
    public long Version { get; private set; }

    public Money ItemsTotal => _lines.Aggregate(Money.Zero, (total, line) => total + line.LineTotal);

    public Money ShippingTotal => Courier.Cost;

    /// <summary>What the buyer pays. Buyer protection is free, per the checkout summary.</summary>
    public Money GrandTotal => ItemsTotal + ShippingTotal;

    /// <summary>
    /// The platform's commission, deducted from the seller's payout rather than added
    /// to the buyer's total.
    /// </summary>
    public Money PlatformFee => ItemsTotal.Percent(PlatformFeePercent);

    /// <summary>Commission rate, matching the fee breakdown on the sell form.</summary>
    public const decimal PlatformFeePercent = 5m;

    /// <summary>What the seller receives once escrow releases.</summary>
    public Money SellerPayout => ItemsTotal - PlatformFee;

    /// <summary>When funds release automatically, once delivery is confirmed.</summary>
    public DateTimeOffset? AutoReleaseAt => DeliveredAt is { } delivered
        ? delivered + AutoReleaseAfterDelivery
        : null;

    /// <summary>Whether the buyer may still act on this order (confirm receipt, dispute).</summary>
    public bool IsOpen => EscrowState
        is EscrowState.Paid or EscrowState.Shipped or EscrowState.InTransit
        or EscrowState.Delivered or EscrowState.Disputed;

    public static Order Create(
        string buyerId,
        string sellerId,
        IEnumerable<OrderLine> lines,
        ShippingAddress shipTo,
        CourierSelection courier,
        IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentNullException.ThrowIfNull(shipTo);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.Equals(buyerId, sellerId, StringComparison.Ordinal))
        {
            throw DomainException.For("order.self_purchase", "You cannot buy your own listing.");
        }

        var materialised = lines?.ToList() ?? throw new ArgumentNullException(nameof(lines));
        if (materialised.Count == 0)
        {
            throw DomainException.For("order.empty", "An order must contain at least one item.");
        }

        var now = clock.UtcNow;
        var id = EntityId.New(now);
        var order = new Order(id, BuildOrderNumber(id), buyerId, sellerId, shipTo, courier, now);
        order._lines.AddRange(materialised);
        return order;
    }

    public static Order Rehydrate(
        string id,
        string orderNumber,
        string buyerId,
        string sellerId,
        IEnumerable<OrderLine> lines,
        ShippingAddress shipTo,
        CourierSelection courier,
        EscrowState escrowState,
        Shipment? shipment,
        Dispute? dispute,
        DateTimeOffset createdAt,
        DateTimeOffset? paidAt,
        DateTimeOffset? shippedAt,
        DateTimeOffset? deliveredAt,
        DateTimeOffset? releasedAt,
        long version)
    {
        var order = new Order(id, orderNumber, buyerId, sellerId, shipTo, courier, createdAt)
        {
            EscrowState = escrowState,
            Shipment = shipment,
            Dispute = dispute,
            PaidAt = paidAt,
            ShippedAt = shippedAt,
            DeliveredAt = deliveredAt,
            ReleasedAt = releasedAt,
            Version = version,
        };

        order._lines.AddRange(lines);
        return order;
    }

    /// <summary>
    /// Records that the payment gateway confirmed settlement, moving funds into escrow.
    /// </summary>
    /// <remarks>
    /// Callable only from the webhook handler, after the payload's HMAC signature has
    /// been verified and the settled amount checked against <see cref="GrandTotal"/>.
    /// Re-notification is common with payment gateways, so a repeat call on an
    /// already-paid order is a no-op rather than an error — that idempotency is what
    /// keeps a retried webhook from double-releasing a listing.
    /// </remarks>
    public void ConfirmPayment(Money settledAmount, IClock clock)
    {
        if (EscrowState != EscrowState.AwaitingPayment)
        {
            if (EscrowState is EscrowState.Paid or EscrowState.Shipped or EscrowState.InTransit
                or EscrowState.Delivered or EscrowState.Released)
            {
                return;
            }

            throw DomainException.For("order.not_payable", $"A {EscrowState} order cannot be paid.");
        }

        if (settledAmount != GrandTotal)
        {
            // An underpayment must never open escrow, and an overpayment signals either a
            // gateway fault or a tampered callback. Both need a human, not a state change.
            throw DomainException.For(
                "order.amount_mismatch",
                $"Settled amount {settledAmount} does not match the order total {GrandTotal}.");
        }

        EscrowState = EscrowState.Paid;
        PaidAt = clock.UtcNow;
        Version++;
    }

    /// <summary>Attaches tracking details and moves the order to shipped.</summary>
    public void MarkShipped(Shipment shipment, string actingSellerId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        EnsureSeller(actingSellerId);

        if (EscrowState != EscrowState.Paid)
        {
            throw DomainException.For("order.not_shippable", $"A {EscrowState} order cannot be marked shipped.");
        }

        if (!string.Equals(shipment.CourierCode, Courier.CourierCode, StringComparison.Ordinal))
        {
            // The buyer paid for a specific service; silently substituting another changes
            // the delivery promise they agreed to.
            throw DomainException.For(
                "order.courier_mismatch",
                "Shipment courier must match the courier the buyer selected at checkout.");
        }

        Shipment = shipment;
        EscrowState = EscrowState.Shipped;
        ShippedAt = clock.UtcNow;
        Version++;
    }

    /// <summary>Records a courier scan showing the parcel is moving.</summary>
    public void MarkInTransit(IClock clock)
    {
        if (EscrowState != EscrowState.Shipped)
        {
            return;
        }

        EscrowState = EscrowState.InTransit;
        Version++;
        _ = clock;
    }

    /// <summary>Records courier-confirmed delivery, starting the auto-release countdown.</summary>
    public void MarkDelivered(IClock clock)
    {
        if (EscrowState is not (EscrowState.Shipped or EscrowState.InTransit))
        {
            throw DomainException.For("order.not_deliverable", $"A {EscrowState} order cannot be marked delivered.");
        }

        EscrowState = EscrowState.Delivered;
        DeliveredAt = clock.UtcNow;
        Version++;
    }

    /// <summary>
    /// Releases escrow to the seller, either by the buyer confirming receipt or by the
    /// auto-release sweep.
    /// </summary>
    /// <param name="confirmedByBuyer">
    /// <c>true</c> for an explicit buyer confirmation, <c>false</c> for the timed sweep.
    /// </param>
    public void Release(bool confirmedByBuyer, IClock clock)
    {
        if (EscrowState == EscrowState.Released)
        {
            return;
        }

        if (EscrowState == EscrowState.Disputed)
        {
            throw DomainException.For(
                "order.disputed",
                "Funds cannot be released while a dispute is open.");
        }

        if (EscrowState != EscrowState.Delivered)
        {
            throw DomainException.For(
                "order.not_releasable",
                $"Funds can only be released after delivery. This order is {EscrowState}.");
        }

        if (!confirmedByBuyer && clock.UtcNow < AutoReleaseAt)
        {
            throw DomainException.For(
                "order.release_window_open",
                "The buyer's confirmation window has not closed yet.");
        }

        EscrowState = EscrowState.Released;
        ReleasedAt = clock.UtcNow;
        Version++;
    }

    /// <summary>Freezes release while the buyer's complaint is reviewed.</summary>
    public void OpenDispute(string reason, string actingBuyerId, IClock clock)
    {
        EnsureBuyer(actingBuyerId);

        if (EscrowState == EscrowState.Released)
        {
            throw DomainException.For(
                "order.already_released",
                "Funds have already been released. Contact support.");
        }

        if (EscrowState is EscrowState.AwaitingPayment or EscrowState.Cancelled or EscrowState.Refunded)
        {
            throw DomainException.For("order.not_disputable", $"A {EscrowState} order cannot be disputed.");
        }

        Dispute = new Dispute(reason, clock.UtcNow, Resolution: null, ResolvedAt: null);
        EscrowState = EscrowState.Disputed;
        Version++;
    }

    /// <summary>Closes a dispute, sending the money to whichever party support ruled for.</summary>
    public void ResolveDispute(DisputeResolution resolution, IClock clock)
    {
        if (EscrowState != EscrowState.Disputed || Dispute is null)
        {
            throw DomainException.For("order.no_open_dispute", "This order has no open dispute.");
        }

        Dispute = Dispute with { Resolution = resolution, ResolvedAt = clock.UtcNow };
        EscrowState = resolution == DisputeResolution.ReleaseToSeller
            ? EscrowState.Released
            : EscrowState.Refunded;

        if (EscrowState == EscrowState.Released)
        {
            ReleasedAt = clock.UtcNow;
        }

        Version++;
    }

    /// <summary>Cancels an order that was never paid for.</summary>
    public void Cancel(IClock clock)
    {
        if (EscrowState != EscrowState.AwaitingPayment)
        {
            throw DomainException.For(
                "order.not_cancellable",
                $"A {EscrowState} order cannot be cancelled. Open a dispute instead.");
        }

        EscrowState = EscrowState.Cancelled;
        Version++;
        _ = clock;
    }

    /// <summary>Whether the unpaid payment window has elapsed.</summary>
    public bool HasPaymentExpired(IClock clock) =>
        EscrowState == EscrowState.AwaitingPayment && clock.UtcNow > CreatedAt + PaymentWindow;

    /// <summary>Confirms the caller is a party to this order, in either role.</summary>
    /// <exception cref="DomainException">The caller is neither buyer nor seller.</exception>
    public void EnsureParticipant(string userId)
    {
        if (!string.Equals(BuyerId, userId, StringComparison.Ordinal)
            && !string.Equals(SellerId, userId, StringComparison.Ordinal))
        {
            throw DomainException.For("order.forbidden", "This order belongs to another user.");
        }
    }

    private void EnsureBuyer(string userId)
    {
        if (!string.Equals(BuyerId, userId, StringComparison.Ordinal))
        {
            throw DomainException.For("order.forbidden", "Only the buyer can perform this action.");
        }
    }

    private void EnsureSeller(string userId)
    {
        if (!string.Equals(SellerId, userId, StringComparison.Ordinal))
        {
            throw DomainException.For("order.forbidden", "Only the seller can perform this action.");
        }
    }

    /// <summary>
    /// Derives the customer-facing order number from the id's trailing characters.
    /// </summary>
    /// <remarks>
    /// Deliberately not a sequential counter. A number like <c>KLK-000412</c> tells any
    /// customer exactly how much business the platform has done and lets anyone probe
    /// adjacent orders. Deriving from the id keeps the reference short and quotable
    /// without leaking volume.
    /// </remarks>
    private static string BuildOrderNumber(string id) => $"KLK-{id[^6..]}";
}

/// <summary>One line of an order, snapshotted at purchase time.</summary>
/// <param name="ListingId">The listing bought.</param>
/// <param name="Title">Title as it read at purchase, so a later edit cannot rewrite history.</param>
/// <param name="PhotoKey">Thumbnail object key at purchase time.</param>
/// <param name="UnitPrice">Price agreed, which may be an accepted offer rather than the asking price.</param>
/// <param name="Quantity">Almost always 1 — collectibles are one-of-one.</param>
public sealed record OrderLine(string ListingId, string Title, string? PhotoKey, Money UnitPrice, int Quantity)
{
    public Money LineTotal => UnitPrice * Quantity;
}

/// <summary>The delivery address, copied at checkout.</summary>
public sealed record ShippingAddress(
    string RecipientName,
    string Phone,
    string Street,
    string City,
    string Province,
    string PostalCode);

/// <summary>The courier service the buyer chose and paid for.</summary>
/// <param name="CourierCode">Machine code, e.g. <c>jne</c>, <c>sicepat</c>, <c>gosend</c>.</param>
/// <param name="ServiceName">Display name, e.g. <c>JNE REG</c>.</param>
/// <param name="Cost">Shipping charge.</param>
/// <param name="EstimatedDays">Quoted transit time, shown as the ETA.</param>
public sealed record CourierSelection(string CourierCode, string ServiceName, Money Cost, int EstimatedDays);

/// <summary>Tracking details and the proof photos the seller must supply.</summary>
/// <param name="CourierCode">Must match the buyer's checkout choice.</param>
/// <param name="TrackingNumber">Courier waybill number.</param>
/// <param name="PackedParcelPhotoKey">Proof photo of the packed parcel.</param>
/// <param name="TrackingLabelPhotoKey">Proof photo of the affixed label.</param>
/// <param name="ShippedOn">Date the seller handed the parcel over.</param>
public sealed record Shipment(
    string CourierCode,
    string TrackingNumber,
    string PackedParcelPhotoKey,
    string TrackingLabelPhotoKey,
    DateOnly ShippedOn)
{
    /// <summary>
    /// Shortest waybill any supported courier issues. The add-tracking form keeps its
    /// submit button disabled below this length.
    /// </summary>
    public const int MinTrackingNumberLength = 8;

    public static Shipment Create(
        string courierCode,
        string trackingNumber,
        string packedParcelPhotoKey,
        string trackingLabelPhotoKey,
        DateOnly shippedOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(courierCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(packedParcelPhotoKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackingLabelPhotoKey);

        var normalised = (trackingNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (normalised.Length < MinTrackingNumberLength)
        {
            throw DomainException.For(
                "shipment.tracking_too_short",
                $"A tracking number must be at least {MinTrackingNumberLength} characters.");
        }

        return new Shipment(courierCode, normalised, packedParcelPhotoKey, trackingLabelPhotoKey, shippedOn);
    }
}

/// <summary>A buyer's complaint against an order.</summary>
public sealed record Dispute(
    string Reason,
    DateTimeOffset OpenedAt,
    DisputeResolution? Resolution,
    DateTimeOffset? ResolvedAt);

/// <summary>How support ruled on a dispute.</summary>
public enum DisputeResolution
{
    /// <summary>The seller delivered as described; funds go to the seller.</summary>
    ReleaseToSeller = 0,

    /// <summary>The buyer's complaint stands; funds return to the buyer.</summary>
    RefundToBuyer = 1,
}
