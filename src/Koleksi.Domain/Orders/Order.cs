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
    /// Two days (the owner's rule, shortened from three) is long enough to open and inspect a
    /// collectible, short enough that sellers can plan around it, and it matches what the order
    /// timeline tells the buyer. The clock starts when the courier reports the parcel delivered,
    /// not when it was shipped.
    /// </remarks>
    public static readonly TimeSpan AutoReleaseAfterDelivery = TimeSpan.FromDays(2);

    /// <summary>
    /// How long a seller has to ship a paid order before the buyer may dispute it for not having shipped.
    /// </summary>
    /// <remarks>
    /// Without it a buyer could freeze the escrow, and put a staff case in front of the seller, the moment they
    /// paid (QA P-04). Three days from payment is the policy default; a dispute on a parcel that has been shipped
    /// is never held back by it.
    /// </remarks>
    public static readonly TimeSpan ShipWindow = TimeSpan.FromDays(3);

    /// <summary>How long an unpaid order holds its stock before auto-cancelling.</summary>
    /// <remarks>Mirrors the 15-minute QRIS countdown shown at checkout stage 2.</remarks>
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the winner of an auction has to pay, counted from when the auction closed (not from
    /// when they open checkout), so a winner cannot stretch it by starting late.
    /// </summary>
    public static readonly TimeSpan AuctionPaymentWindow = TimeSpan.FromMinutes(30);

    /// <summary>How often a shipped parcel is asked about while it has not been reported delivered.</summary>
    public static readonly TimeSpan TrackingPollInterval = TimeSpan.FromMinutes(30);

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
        PaymentDueAt = createdAt + PaymentWindow;
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

    /// <summary>Human-facing reference shown on receipts, e.g. <c>KLK-8F2A41QZ</c> (older orders have six characters).</summary>
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

    /// <summary>
    /// What this purchase came from. A plain sale that is never paid is just a lapsed reservation; an
    /// accepted nego offer or an auction win that is never paid is a broken commitment and earns the
    /// buyer a warning (see <c>AccountStanding</c>).
    /// </summary>
    public OrderSource Source { get; private set; } = OrderSource.FixedPrice;

    /// <summary>
    /// The deal behind a non-sale order: the offer's id for an accepted offer, the listing's id for an
    /// auction win. It is what makes one broken deal one warning (see <c>AccountStanding</c>), however
    /// many orders were opened for it. <c>null</c> for a plain sale.
    /// </summary>
    public string? SourceReference { get; private set; }

    /// <summary>
    /// When an unpaid order stops being payable and its stock goes back on sale: fifteen minutes after
    /// checkout for a sale or an accepted offer, thirty minutes after the close for an auction win.
    /// Orders stored before this was kept read as created + <see cref="PaymentWindow"/>.
    /// </summary>
    public DateTimeOffset PaymentDueAt { get; private set; }

    /// <summary>When the courier was last asked about this parcel, or <c>null</c> if it never has been.</summary>
    public DateTimeOffset? TrackingCheckedAt { get; private set; }

    /// <summary>When the courier should next be asked about a shipped parcel; <c>null</c> in any other state.</summary>
    public DateTimeOffset? NextTrackingCheckAt => EscrowState is EscrowState.Shipped or EscrowState.InTransit
        ? (TrackingCheckedAt ?? ShippedAt) + TrackingPollInterval
        : null;

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
    public Money PlatformFee => ItemsTotal.Percent(AppliedFeePercent);

    /// <summary>
    /// The commission rate in force today, matching the fee breakdown on the sell form. Applied to
    /// every order created from now on; an existing order keeps the rate it was created with
    /// (<see cref="AppliedFeePercent"/>).
    /// </summary>
    public const decimal PlatformFeePercent = 5m;

    /// <summary>
    /// The commission rate this order was created under, snapshotted so that changing
    /// <see cref="PlatformFeePercent"/> later cannot rewrite what a past order's seller was owed.
    /// Orders stored before the snapshot existed have none and read as the current rate.
    /// </summary>
    public decimal AppliedFeePercent => FeePercentSnapshot ?? PlatformFeePercent;

    /// <summary>The stored snapshot itself; <c>null</c> on an order that predates it.</summary>
    public decimal? FeePercentSnapshot { get; private set; }

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
        IClock clock,
        OrderSource source = OrderSource.FixedPrice,
        DateTimeOffset? paymentDueAt = null,
        string? sourceReference = null)
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

        // Money arithmetic is checked, so a total beyond a long would throw OverflowException
        // (an opaque 500) the first time anything read it. Refused here as a coded domain error
        // instead, while nothing has been stored or held (QA D-07). Listing prices are capped far
        // below this; it guards the one path that is not, an auction won at an enormous bid.
        try
        {
            _ = order.GrandTotal;
        }
        catch (OverflowException)
        {
            throw DomainException.For("order.total_too_large", "The order total is too large.");
        }

        order.FeePercentSnapshot = PlatformFeePercent;
        order.Source = source;
        order.SourceReference = sourceReference;
        if (paymentDueAt is { } due)
        {
            if (due <= now)
            {
                throw DomainException.For("order.payment_window_closed", "The time to pay for this purchase has run out.");
            }

            order.PaymentDueAt = due;
        }

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
        long version,
        decimal? platformFeePercent = null,
        OrderSource source = OrderSource.FixedPrice,
        DateTimeOffset? paymentDueAt = null,
        DateTimeOffset? trackingCheckedAt = null,
        string? sourceReference = null)
    {
        var order = new Order(id, orderNumber, buyerId, sellerId, shipTo, courier, createdAt)
        {
            FeePercentSnapshot = platformFeePercent,
            Source = source,
            SourceReference = sourceReference,
            PaymentDueAt = paymentDueAt ?? createdAt + PaymentWindow,
            TrackingCheckedAt = trackingCheckedAt,
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

    /// <summary>Notes that the courier was asked and has not delivered yet, so the next question waits its turn.</summary>
    public void RecordTrackingCheck(IClock clock)
    {
        if (EscrowState is not (EscrowState.Shipped or EscrowState.InTransit))
        {
            return;
        }

        TrackingCheckedAt = clock.UtcNow;
        Version++;
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

        // The buyer is the authority on their own parcel: confirming receipt releases the money even if
        // the courier has not reported the delivery (or never will, with no tracking provider). The timed
        // release is different: it only ever starts from a courier-reported delivery.
        if (confirmedByBuyer && EscrowState is EscrowState.Shipped or EscrowState.InTransit)
        {
            DeliveredAt = clock.UtcNow;
            EscrowState = EscrowState.Delivered;
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

    /// <summary>
    /// When a buyer may first dispute an order that has been paid but not shipped (<see cref="ShipWindow"/> after payment);
    /// <c>null</c> before payment. Once the parcel is shipped a dispute is open at any time.
    /// </summary>
    public DateTimeOffset? DisputableFrom => PaidAt + ShipWindow;

    /// <summary>
    /// Refuses a buyer's dispute on a paid order the seller has not yet had <see cref="ShipWindow"/> to ship. Called where a
    /// buyer opens a dispute (<c>OpenOrderDisputeHandler</c>); <see cref="OpenDispute"/> itself stays state-only so that the
    /// rest of the system, and its tests, can still put an order into dispute at any time.
    /// </summary>
    /// <exception cref="DomainException"><c>order.dispute_too_early</c>.</exception>
    public void EnsureDisputeWindowOpen(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (EscrowState == EscrowState.Paid && DisputableFrom is { } from && clock.UtcNow < from)
        {
            throw DomainException.For(
                "order.dispute_too_early",
                "The seller still has time to ship this order. You can open a dispute if it is not shipped by then.");
        }
    }

    /// <summary>Freezes release while the buyer's complaint is reviewed.</summary>
    public void OpenDispute(string reason, string actingBuyerId, IClock clock)
    {
        EnsureBuyer(actingBuyerId);

        var trimmedReason = (reason ?? string.Empty).Trim();
        if (trimmedReason.Length == 0)
        {
            throw DomainException.For("order.dispute_reason_required", "Say what is wrong with the order.");
        }

        if (trimmedReason.Length > Dispute.MaxReasonLength)
        {
            throw DomainException.For(
                "order.dispute_reason_too_long",
                $"The reason must be at most {Dispute.MaxReasonLength} characters.");
        }

        // The first reason is the evidence: a second submission must not overwrite it (nor reset
        // when the dispute was opened) just because the order is still in the Disputed state.
        if (EscrowState == EscrowState.Disputed)
        {
            throw DomainException.For("order.already_disputed", "A dispute is already open on this order.");
        }

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

        Dispute = new Dispute(trimmedReason, clock.UtcNow, Resolution: null, ResolvedAt: null);
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
        EscrowState == EscrowState.AwaitingPayment && clock.UtcNow > PaymentDueAt;

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
    private static string BuildOrderNumber(string id) => $"KLK-{id[^OrderNumberLength..]}";

    /// <summary>
    /// Characters of the id kept in the order number: 8 Crockford base-32 characters are 40 random
    /// bits (the id's tail is its random part), so a collision needs on the order of a million
    /// orders rather than the thousands 6 characters (28 to 30 bits) allowed (QA D-13). Numbers
    /// issued with 6 characters stay valid: nothing parses the length, and lookup is by the whole
    /// stored string.
    /// </summary>
    internal const int OrderNumberLength = 8;
}

/// <summary>Where an order's price and seller came from.</summary>
public enum OrderSource
{
    /// <summary>The listing's asking price. Walking away from it unpaid is not a violation.</summary>
    FixedPrice = 0,

    /// <summary>An offer the seller accepted. A deal both sides agreed to: not paying for it is a violation.</summary>
    AcceptedOffer = 1,

    /// <summary>An auction the buyer won or bought out. Not paying for it is a violation.</summary>
    AuctionWin = 2,
}

/// <summary>One line of an order, snapshotted at purchase time.</summary>
/// <param name="ListingId">The listing bought.</param>
/// <param name="Title">Title as it read at purchase, so a later edit cannot rewrite history.</param>
/// <param name="PhotoKey">Thumbnail object key at purchase time.</param>
/// <param name="UnitPrice">Price agreed, which may be an accepted offer rather than the asking price.</param>
/// <param name="Quantity">Units bought. Always 1 for an accepted offer or an auction; a sale may buy several when the seller listed stock.</param>
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

    /// <summary>Longest waybill accepted. Generous for any courier, small enough to bound what is stored.</summary>
    public const int MaxTrackingNumberLength = 64;

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

        // Two photos, two views of the shipment: one image in both slots is one proof pretending to be two.
        if (string.Equals(packedParcelPhotoKey, trackingLabelPhotoKey, StringComparison.Ordinal))
        {
            throw DomainException.For(
                "shipment.proof_duplicate",
                "The packed-parcel photo and the tracking-label photo must be two different photos.");
        }

        var normalised = (trackingNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (normalised.Length < MinTrackingNumberLength)
        {
            throw DomainException.For(
                "shipment.tracking_too_short",
                $"A tracking number must be at least {MinTrackingNumberLength} characters.");
        }

        if (normalised.Length > MaxTrackingNumberLength)
        {
            throw DomainException.For(
                "shipment.tracking_too_long",
                $"A tracking number must be at most {MaxTrackingNumberLength} characters.");
        }

        return new Shipment(courierCode, normalised, packedParcelPhotoKey, trackingLabelPhotoKey, shippedOn);
    }
}

/// <summary>A buyer's complaint against an order.</summary>
public sealed record Dispute(
    string Reason,
    DateTimeOffset OpenedAt,
    DisputeResolution? Resolution,
    DateTimeOffset? ResolvedAt)
{
    /// <summary>Longest complaint accepted; bounds what a single order row can grow to.</summary>
    public const int MaxReasonLength = 1000;
}

/// <summary>How support ruled on a dispute.</summary>
public enum DisputeResolution
{
    /// <summary>The seller delivered as described; funds go to the seller.</summary>
    ReleaseToSeller = 0,

    /// <summary>The buyer's complaint stands; funds return to the buyer.</summary>
    RefundToBuyer = 1,
}
