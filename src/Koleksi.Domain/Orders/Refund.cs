using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;

namespace Koleksi.Domain.Orders;

/// <summary>Where a refund stands. No value here means money moved: that needs a real payment gateway, which is not connected.</summary>
public enum RefundStatus
{
    /// <summary>Written down with the ruling. Nothing has tried to send it yet.</summary>
    Recorded = 0,

    /// <summary>The real sender was asked and has no gateway to send through: recorded, payment pending gateway.</summary>
    PendingGateway = 1,

    /// <summary>The development stand-in accepted it. Still no money moved; it only rehearses the flow.</summary>
    Simulated = 2,
}

/// <summary>What became of the part of the order the refund did not cover (the order total less the refund).</summary>
public enum RemainderDisposition
{
    /// <summary>There is no remainder: the buyer was refunded the whole order. Also how a refund recorded before remainders existed reads.</summary>
    None = 0,

    /// <summary>Kept back for a later decision. Nobody is owed it yet; staff settle it with <see cref="Refund.SettleRemainder"/>.</summary>
    Held = 1,

    /// <summary>Released to the seller, less the platform fee. A <see cref="Payout"/> is owed.</summary>
    ReleasedToSeller = 2,

    /// <summary>Split: staff named what the seller is owed, and the platform keeps the rest. A <see cref="Payout"/> is owed.</summary>
    Split = 3,
}

/// <summary>What staff choose to do with the remainder. <see cref="Hold"/> is the default for a partial refund.</summary>
public enum RemainderMode
{
    Hold = 0,
    Release = 1,
    Split = 2,
}

/// <summary>Staff's choice for the remainder. <paramref name="SellerPayout"/> is given for <see cref="RemainderMode.Split"/> and for nothing else.</summary>
public sealed record RemainderChoice(RemainderMode Mode, long? SellerPayout = null);

/// <summary>What a choice comes to in money.</summary>
/// <param name="SellerPayout">What the seller is owed from the remainder. Zero unless released or split.</param>
/// <param name="Fee">What the platform keeps of it, for the record on the payout.</param>
public readonly record struct RemainderPlan(RemainderDisposition Disposition, Money SellerPayout, Money Fee);

/// <summary>
/// A refund to the buyer that staff decided on when they ruled for the buyer in a dispute.
/// </summary>
/// <remarks>
/// <para>
/// <b>The amount is the staff member's decision</b>, any whole-rupiah amount from 1 up to what the buyer paid for the order
/// (items plus shipping, <see cref="Order.GrandTotal"/>), never more. The ceiling is checked here, on the order the API
/// itself read, and never trusted from the page.
/// </para>
/// <para>
/// The record is the ledger entry: it is written in the same transaction as the order's new state, the way a payout is
/// written with a release. Sending the money is a separate step through <c>IRefundSender</c>. Refunding an order that was
/// already released to the seller is out of scope here: a released order is not disputable (<see cref="Order.OpenDispute"/>).
/// </para>
/// <para>
/// <b>The remainder.</b> A refund of less than the order total leaves a remainder, and staff decide what becomes of it when
/// they rule: release it to the seller (less the platform fee at the order's own rate), split it (staff name what the seller is
/// owed and the platform keeps the rest), or hold it for a later decision (the default; <see cref="SettleRemainder"/>). The seller
/// is never owed more from a remainder than a normal sale of the order would have paid them (<see cref="Order.SellerPayout"/>), and
/// the buyer's refund plus the seller's payout never exceed what the buyer paid.
/// </para>
/// </remarks>
public sealed class Refund
{
    private Refund(
        string id,
        string orderId,
        string orderNumber,
        string buyerId,
        Money amount,
        string reason,
        string staffId,
        string staffName,
        DateTimeOffset createdAt,
        RefundStatus status,
        DateTimeOffset statusAt,
        Money remainder,
        RemainderDisposition disposition,
        Money sellerPayout,
        DateTimeOffset? settledAt)
    {
        Id = id;
        OrderId = orderId;
        OrderNumber = orderNumber;
        BuyerId = buyerId;
        Amount = amount;
        Reason = reason;
        StaffId = staffId;
        StaffName = staffName;
        CreatedAt = createdAt;
        Status = status;
        StatusAt = statusAt;
        Remainder = remainder;
        Disposition = disposition;
        SellerPayout = sellerPayout;
        SettledAt = settledAt;
    }

    /// <summary>The order total less the refund: what the buyer paid and was not refunded.</summary>
    public Money Remainder { get; }

    public RemainderDisposition Disposition { get; private set; }

    /// <summary>What the seller is owed out of the remainder. Zero unless it was released or split.</summary>
    public Money SellerPayout { get; private set; }

    /// <summary>When a held remainder was settled. Null if it never was held, or still is.</summary>
    public DateTimeOffset? SettledAt { get; private set; }

    public string Id { get; }

    public string OrderId { get; }

    public string OrderNumber { get; }

    public string BuyerId { get; }

    public Money Amount { get; }

    public string Reason { get; }

    public string StaffId { get; }

    public string StaffName { get; }

    public DateTimeOffset CreatedAt { get; }

    public RefundStatus Status { get; private set; }

    /// <summary>When <see cref="Status"/> last changed.</summary>
    public DateTimeOffset StatusAt { get; private set; }

    /// <summary>
    /// Records the refund for a just-resolved order.
    /// </summary>
    /// <param name="order">The order after <see cref="Order.ResolveDispute"/> ruled for the buyer.</param>
    /// <param name="amount">Whole rupiah, 1 to the order's grand total.</param>
    /// <exception cref="DomainException">
    /// <c>dispute.refund_amount_invalid</c> (below 1 or above the total), <c>admin.reason_invalid</c>, or
    /// <c>refund.order_not_refunded</c> when the order was not refunded by a dispute ruling.
    /// </exception>
    /// <param name="remainder">What to do with the part of the order the refund does not cover. Leave it out for a full refund (nothing is left) or to hold it.</param>
    /// <exception cref="DomainException">
    /// Also <c>dispute.remainder_invalid</c> (a choice for a refund that leaves nothing, a seller amount on anything but a split) and
    /// <c>dispute.remainder_amount_invalid</c> (a split amount below 1 or above what the seller could be owed).
    /// </exception>
    public static Refund Record(
        Order order, long amount, string? reason, string staffId, string staffName, IClock clock, RemainderChoice? remainder = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(staffId);
        ArgumentException.ThrowIfNullOrWhiteSpace(staffName);
        ArgumentNullException.ThrowIfNull(clock);

        if (order.EscrowState != EscrowState.Refunded || order.Dispute is not { Resolution: DisputeResolution.RefundToBuyer })
        {
            throw DomainException.For("refund.order_not_refunded", "Only an order refunded by a dispute ruling can have a refund.");
        }

        var total = order.GrandTotal;
        if (amount < 1 || amount > total.Rupiah)
        {
            throw DomainException.For(
                "dispute.refund_amount_invalid",
                $"The refund must be from Rp1 to {total} (what the buyer paid for this order).");
        }

        var trimmed = AdminReason.Require(reason);
        var now = clock.UtcNow;
        var left = total - Money.FromRupiah(amount);
        var plan = PlanRemainder(order, left, remainder, settling: false);

        return new Refund(
            EntityId.New(now), order.Id, order.OrderNumber, order.BuyerId, Money.FromRupiah(amount), trimmed,
            staffId, staffName.Trim(), now, RefundStatus.Recorded, now, left, plan.Disposition, plan.SellerPayout, settledAt: null);
    }

    /// <summary>Rebuilds a stored refund. For the persistence layer only; nothing is re-validated.</summary>
    /// <remarks>A refund stored before remainders existed has none of the last four values, and reads as having no remainder.</remarks>
    public static Refund Rehydrate(
        string id,
        string orderId,
        string orderNumber,
        string buyerId,
        Money amount,
        string reason,
        string staffId,
        string staffName,
        DateTimeOffset createdAt,
        RefundStatus status,
        DateTimeOffset statusAt,
        Money? remainder = null,
        RemainderDisposition disposition = RemainderDisposition.None,
        Money? sellerPayout = null,
        DateTimeOffset? settledAt = null) =>
        new(id, orderId, orderNumber, buyerId, amount, reason, staffId, staffName, createdAt, status, statusAt,
            remainder ?? Money.Zero, disposition, sellerPayout ?? Money.Zero, settledAt);

    /// <summary>
    /// What a choice for the remainder comes to, or the error that refuses it.
    /// </summary>
    /// <param name="left">The order total less the refund.</param>
    /// <param name="settling">True when settling a held remainder: holding it again is not a settlement.</param>
    private static RemainderPlan PlanRemainder(Order order, Money left, RemainderChoice? choice, bool settling)
    {
        if (left.IsZero)
        {
            return choice is null
                ? new RemainderPlan(RemainderDisposition.None, Money.Zero, Money.Zero)
                : throw DomainException.For("dispute.remainder_invalid", "A full refund leaves nothing over, so there is nothing to decide.");
        }

        choice ??= new RemainderChoice(RemainderMode.Hold);

        // A seller is never owed more from a remainder than the sale itself would have paid them.
        var ceiling = left.CompareTo(order.SellerPayout) <= 0 ? left : order.SellerPayout;

        switch (choice.Mode)
        {
            case RemainderMode.Hold:
                if (settling || choice.SellerPayout is not null)
                {
                    throw DomainException.For("dispute.remainder_invalid", settling
                        ? "Settling a held remainder means releasing it or splitting it."
                        : "Holding the remainder owes the seller nothing, so it carries no amount.");
                }

                return new RemainderPlan(RemainderDisposition.Held, Money.Zero, Money.Zero);

            case RemainderMode.Release:
            {
                if (choice.SellerPayout is not null)
                {
                    throw DomainException.For("dispute.remainder_invalid", "Releasing the remainder works out the seller's amount; it is not typed in.");
                }

                var fee = left.Percent(order.AppliedFeePercent);
                var owed = left - fee;
                if (owed.CompareTo(ceiling) > 0)
                {
                    owed = ceiling;
                }

                return owed.IsZero
                    ? throw DomainException.For("dispute.remainder_amount_invalid", "Nothing would be left for the seller after the fee.")
                    : new RemainderPlan(RemainderDisposition.ReleasedToSeller, owed, fee);
            }

            case RemainderMode.Split:
            {
                if (choice.SellerPayout is not { } typed || typed < 1 || typed > ceiling.Rupiah)
                {
                    throw DomainException.For(
                        "dispute.remainder_amount_invalid",
                        $"The seller's share must be from Rp1 to {ceiling} (the remainder, and no more than a normal sale would pay them).");
                }

                return new RemainderPlan(RemainderDisposition.Split, Money.FromRupiah(typed), left - Money.FromRupiah(typed));
            }

            default:
                throw DomainException.For("dispute.remainder_invalid", "Unknown way to handle the remainder.");
        }
    }

    /// <summary>
    /// What the seller is owed out of the remainder, as a payout: <c>null</c> unless it was released or split. One payout per order,
    /// enforced by the storage key, so it can only ever be written once.
    /// </summary>
    public Payout? RemainderPayout(Order order, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(clock);

        if (Disposition is not (RemainderDisposition.ReleasedToSeller or RemainderDisposition.Split))
        {
            return null;
        }

        var fee = Disposition == RemainderDisposition.ReleasedToSeller ? Remainder.Percent(order.AppliedFeePercent) : Remainder - SellerPayout;
        return Payout.ForRemainder(order, SellerPayout, fee, clock);
    }

    /// <summary>
    /// Decides a held remainder: release it to the seller or split it. Moves it once; call <see cref="RemainderPayout"/> for the payout.
    /// </summary>
    /// <exception cref="DomainException">
    /// <c>dispute.remainder_not_held</c> when there is nothing held (a full refund, an earlier refund, or already settled), and
    /// the same errors as <see cref="Record"/> for the choice itself.
    /// </exception>
    public void SettleRemainder(Order order, RemainderChoice choice, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(clock);

        if (Disposition != RemainderDisposition.Held)
        {
            throw DomainException.For("dispute.remainder_not_held", "This refund has no held remainder to settle.");
        }

        var plan = PlanRemainder(order, Remainder, choice, settling: true);
        Disposition = plan.Disposition;
        SellerPayout = plan.SellerPayout;
        SettledAt = clock.UtcNow;
    }

    /// <summary>
    /// Notes what the sender did with a refund that was only recorded. Moves it once: a refund already past
    /// <see cref="RefundStatus.Recorded"/> keeps its status, so a repeated send cannot rewrite history.
    /// </summary>
    public void RecordOutcome(RefundStatus outcome, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (outcome == RefundStatus.Recorded)
        {
            throw DomainException.For("refund.outcome_invalid", "An outcome must be pending gateway or simulated.");
        }

        if (Status != RefundStatus.Recorded)
        {
            return;
        }

        Status = outcome;
        StatusAt = clock.UtcNow;
    }
}
