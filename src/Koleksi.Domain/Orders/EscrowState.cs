namespace Koleksi.Domain.Orders;

/// <summary>
/// Where an order's money sits.
/// </summary>
/// <remarks>
/// <para>
/// The design treats escrow as structural rather than decorative — the same state is
/// rendered on the item rail, in checkout, on the order timeline, and in the payout
/// ledger. One enum drives all four, so those surfaces cannot drift apart.
/// </para>
/// <para>
/// The five steps the buyer sees on the tracking timeline are
/// <see cref="Paid"/> → <see cref="Shipped"/> → <see cref="InTransit"/> →
/// <see cref="Delivered"/> → <see cref="Released"/>. The remaining members are terminal
/// or exceptional branches that the timeline renders as an interruption.
/// </para>
/// <para>
/// Numeric values are persisted, so they are assigned explicitly and must never be
/// renumbered.
/// </para>
/// </remarks>
public enum EscrowState
{
    /// <summary>Order created; the buyer has not completed payment. Nothing is reserved.</summary>
    AwaitingPayment = 0,

    /// <summary>Funds received and held by the platform. The seller is cleared to ship.</summary>
    Paid = 1,

    /// <summary>The seller has submitted a tracking number and shipping proof.</summary>
    Shipped = 2,

    /// <summary>The courier has confirmed movement.</summary>
    InTransit = 3,

    /// <summary>The courier reports delivery. The auto-release countdown starts here.</summary>
    Delivered = 4,

    /// <summary>Funds moved to the seller's payout balance. Terminal.</summary>
    Released = 5,

    /// <summary>The buyer raised a dispute. Release is frozen pending resolution.</summary>
    Disputed = 6,

    /// <summary>Funds returned to the buyer. Terminal.</summary>
    Refunded = 7,

    /// <summary>Cancelled before payment, or auto-cancelled when the payment window expired. Terminal.</summary>
    Cancelled = 8,
}
