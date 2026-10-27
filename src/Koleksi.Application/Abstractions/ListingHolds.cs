namespace Koleksi.Application.Abstractions;

/// <summary>
/// Stock: the units of a listing, and the short reservation a buyer's order takes on them while it
/// awaits payment.
/// </summary>
/// <remarks>
/// <para>
/// Every non-auction listing has a stock (how many units the seller has); an auction has exactly one.
/// A unit is in one of three states: <b>free</b> (nobody has it), <b>held</b> (an order reserved it and
/// is being paid) or <b>sold</b> (the order was paid). Checkout reserves the units first and only then
/// creates the order, so a second buyer is refused the moment the last free unit is taken, instead of
/// racing to pay for something already gone.
/// </para>
/// <para>
/// A hold lapses by itself at the time the caller gave (the order's own payment deadline: fifteen
/// minutes for a sale or an accepted offer, thirty for an auction win), so an abandoned checkout frees
/// its stock with no cleanup job, and exactly on time. The same buyer checking out again replaces
/// their own earlier hold (the orders it belonged to are reported back so they can be cancelled).
/// </para>
/// <para>
/// Renewal is bounded: however often the same buyer checks out again, a hold never reaches past
/// <see cref="MaxTotalDuration"/> after it was first taken. Otherwise one account re-submitting every
/// few minutes could keep a listing out of everyone else's reach indefinitely without ever paying.
/// </para>
/// </remarks>
public interface IListingHoldStore
{
    /// <summary>How long a one-unit hold lasts when the caller does not say, matching <c>Order.PaymentWindow</c>.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

    /// <summary>The longest a hold can be kept alive by renewals, counted from when it was first taken.</summary>
    public static readonly TimeSpan MaxTotalDuration = TimeSpan.FromMinutes(30);

    /// <summary>The most units one listing can have; bounds the rows read for every reservation.</summary>
    public const int MaxStock = 99;

    /// <summary>The most units one order can reserve.</summary>
    public const int MaxQuantityPerOrder = 20;

    /// <summary>
    /// Reserves <paramref name="quantity"/> units of a listing that has <paramref name="stock"/> units in
    /// all, for <paramref name="buyerId"/>, until <paramref name="heldUntil"/>. Atomic: either every unit
    /// is taken or none is, and two buyers racing for the last unit cannot both get it.
    /// </summary>
    Task<HoldResult> TryReserveAsync(
        string listingId, int stock, string buyerId, string orderId, int quantity, DateTimeOffset heldUntil, CancellationToken ct);

    /// <summary>
    /// The single-unit shortcut: one unit of a one-unit listing, held for <see cref="Duration"/>.
    /// </summary>
    Task<HoldResult> TryHoldAsync(string listingId, string buyerId, string orderId, CancellationToken ct);

    /// <summary>How many units are free, held and sold right now. Read-only; takes nothing.</summary>
    Task<StockAvailability> GetAvailabilityAsync(string listingId, int stock, CancellationToken ct);

    /// <summary>
    /// Marks the order's reserved units sold, because it was paid. Idempotent: a repeated payment
    /// notification counts the same units again. If the hold lapsed but the units are still free (nobody
    /// else reserved them), they are claimed now; payment is authoritative.
    /// </summary>
    /// <returns>How many units this order now owns as sold; less than <paramref name="quantity"/> means some were lost to another buyer.</returns>
    Task<int> CommitSaleAsync(
        string listingId, int stock, string buyerId, string orderId, int quantity, CancellationToken ct);

    /// <summary>
    /// Whether any unit of the listing is held right now (a hold that has not lapsed). Read-only; it takes
    /// nothing. Used to refuse delisting an item a buyer is in the middle of paying for.
    /// </summary>
    Task<bool> IsHeldAsync(string listingId, CancellationToken ct);

    /// <summary>Gives back the units this order reserved (a cancelled or expired order). Idempotent.</summary>
    Task ReleaseAsync(string listingId, string buyerId, string orderId, CancellationToken ct);
}

/// <param name="Acquired"><c>false</c> when there were not enough free units.</param>
/// <param name="ReplacedOrderId">
/// When the same buyer already held units, the (first) order that hold belonged to. It is superseded by
/// the new one and the caller should cancel it if it is still unpaid.
/// </param>
/// <param name="HeldUntil">When the hold lapses, when it was acquired.</param>
/// <param name="ReplacedOrderIds">Every order of the same buyer whose hold this replaced.</param>
/// <param name="Available">Units free for someone else after this call (or, on a refusal, units that were free).</param>
public sealed record HoldResult(
    bool Acquired,
    string? ReplacedOrderId,
    DateTimeOffset? HeldUntil = null,
    IReadOnlyList<string>? ReplacedOrderIds = null,
    int Available = 0);

/// <param name="Stock">Units the seller has listed in all.</param>
/// <param name="Sold">Units paid for.</param>
/// <param name="Held">Units reserved by an unpaid order whose time has not run out.</param>
/// <param name="HighestUsedIndex">The highest unit slot that is sold or held; lowering the stock below it is refused. <c>-1</c> when none.</param>
public sealed record StockAvailability(int Stock, int Sold, int Held, int HighestUsedIndex = -1)
{
    /// <summary>Units a new buyer could reserve right now.</summary>
    public int Available => Math.Max(0, Stock - Sold - Held);

    /// <summary>Whether every unit has been paid for.</summary>
    public bool SoldOut => Sold >= Stock;

    /// <summary>The smallest stock a seller may lower this listing to.</summary>
    public int MinimumStock => Math.Max(Sold + Held, HighestUsedIndex + 1);
}
