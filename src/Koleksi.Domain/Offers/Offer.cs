using Koleksi.Domain.Common;

namespace Koleksi.Domain.Offers;

/// <summary>Where an offer stands. Drives the status badges on the "My offers" and "Offers received" tabs.</summary>
public enum OfferStatus
{
    /// <summary>The buyer's offer, awaiting the seller. Still within its validity window.</summary>
    Pending = 0,

    /// <summary>The seller proposed a different price; the ball is back with the buyer.</summary>
    Countered = 1,

    /// <summary>Agreed. The buyer may now check out, once, at <see cref="Offer.CurrentAmount"/>.</summary>
    Accepted = 2,

    /// <summary>Refused by the seller (or auto-declined below their threshold), or by the buyer of a counter.</summary>
    Declined = 3,

    /// <summary>The validity window closed with no response, or an accepted offer went unpaid for too long.</summary>
    Expired = 4,

    /// <summary>Retracted by the buyer before the deal was struck.</summary>
    Withdrawn = 5,

    /// <summary>An order was created from the accepted offer. Terminal: an offer pays for one order.</summary>
    CheckedOut = 6,
}

/// <summary>Which side of an offer a user is on.</summary>
public enum OfferParty
{
    Buyer = 0,
    Seller = 1,
}

/// <summary>
/// A buyer's proposal to buy a <c>nego</c> listing below its asking price, and the
/// negotiation that follows.
/// </summary>
/// <remarks>
/// <para>
/// The whole state machine, deliberately small:
/// </para>
/// <code>
/// Pending   --seller accept--&gt;  Accepted
///           --seller decline--&gt; Declined
///           --seller counter--&gt; Countered      (once; a counter is take-it-or-leave-it)
///           --buyer withdraw--&gt; Withdrawn
/// Countered --buyer accept--&gt;   Accepted       (at the counter amount)
///           --buyer decline--&gt;  Declined
///           --buyer withdraw--&gt; Withdrawn
/// Accepted  --checkout--&gt;       CheckedOut     (exactly once; see MarkCheckedOut)
/// Pending | Countered | Accepted --deadline passes--&gt; Expired
/// </code>
/// <para>
/// Whoever's turn it is, is the only party who can act: the seller on <c>Pending</c>, the
/// buyer on <c>Countered</c>. Letting the seller "accept" a row they already countered would
/// commit the buyer to a price without their consent.
/// </para>
/// <para>
/// <see cref="ExpiresAt"/> is the deadline for whatever is awaited next: the seller's reply
/// (<see cref="Validity"/>), the buyer's reply to a counter (<see cref="Validity"/> again),
/// or the buyer's checkout once accepted (<see cref="CheckoutWindow"/>). An offer that never
/// expires is a free option on a one-of-one item, and an accepted one that is never paid for
/// would hold the item off the market indefinitely.
/// </para>
/// <para>
/// Expiry is lazy: nothing runs on a timer. <see cref="Expire"/> is idempotent and can be
/// called by a future sweep, but every read and every action also applies it, so a lapsed
/// offer is never shown or acted on as live.
/// </para>
/// <para>
/// An accepted offer is a price commitment, not a purchase. The buyer still checks out, and
/// the order carries the agreed price as its unit price.
/// </para>
/// </remarks>
public sealed class Offer
{
    /// <summary>How long a reply is awaited, matching the modal's stated 24 hours.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a buyer has to check out once an offer is accepted.
    /// </summary>
    /// <remarks>
    /// Longer than <see cref="Validity"/> because paying means arranging funds and an address,
    /// not just tapping a button; short enough that an accepted deal cannot sit on a one-of-one
    /// item for a week while the seller waits.
    /// </remarks>
    public static readonly TimeSpan CheckoutWindow = TimeSpan.FromHours(48);

    /// <summary>
    /// The floor for any offer, as a share of the asking price.
    /// </summary>
    /// <remarks>
    /// Below a fifth of asking, an offer is noise rather than negotiation. Sellers in
    /// the prototype's copy complain about exactly this, and a hard floor costs nothing
    /// to enforce. Sellers may set a stricter per-listing threshold on top.
    /// </remarks>
    public const decimal AbsoluteMinimumPercent = 20m;

    private Offer(
        string id,
        string listingId,
        string buyerId,
        string sellerId,
        Money amount,
        Money askingPrice,
        string? message,
        DateTimeOffset createdAt)
    {
        Id = id;
        ListingId = listingId;
        BuyerId = buyerId;
        SellerId = sellerId;
        Amount = amount;
        AskingPrice = askingPrice;
        Message = message;
        CreatedAt = createdAt;
        ExpiresAt = createdAt + Validity;
        Status = OfferStatus.Pending;
    }

    public string Id { get; }
    public string ListingId { get; }
    public string BuyerId { get; }
    public string SellerId { get; }

    /// <summary>
    /// What the buyer offered. Never overwritten: a counter is recorded beside it in
    /// <see cref="CounterAmount"/> so the history of the negotiation survives.
    /// </summary>
    public Money Amount { get; }

    /// <summary>The seller's counter, once they have made one. At most one per offer.</summary>
    public Money? CounterAmount { get; private set; }

    /// <summary>
    /// The price that is on the table now, and the price paid if the offer is accepted: the
    /// counter once there is one, otherwise the buyer's own amount.
    /// </summary>
    public Money CurrentAmount => CounterAmount ?? Amount;

    /// <summary>The listing's asking price when the offer was made, for the "% of asking" hint.</summary>
    public Money AskingPrice { get; }

    /// <summary>Optional note from the buyer. Length-capped and rendered as plain text.</summary>
    public string? Message { get; }

    /// <summary>The buyer's public handle when the offer was made, so the seller can see who it is from.</summary>
    /// <remarks>A snapshot, like a conversation's: <c>null</c> on offers stored before it existed.</remarks>
    public string? BuyerUsername { get; private set; }

    /// <summary>The seller's public handle when the offer was made. <c>null</c> on older offers.</summary>
    public string? SellerUsername { get; private set; }

    /// <summary>The order created from this offer, once checked out.</summary>
    public string? OrderId { get; private set; }

    public OfferStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>The deadline for whatever is awaited next. See the class remarks.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RespondedAt { get; private set; }
    public long Version { get; private set; }

    /// <summary>The buyer's offer as a percentage of asking. The modal turns this accent above 85%.</summary>
    public decimal PercentOfAsking => Amount.AsPercentageOf(AskingPrice);

    /// <summary>Whether the offer is still unresolved and in time, awaiting a reply right now.</summary>
    public bool IsActionable(IClock clock) =>
        Status is OfferStatus.Pending or OfferStatus.Countered && clock.UtcNow < ExpiresAt;

    /// <summary>
    /// The status as the clock sees it: an unresolved offer past its deadline reads as
    /// <see cref="OfferStatus.Expired"/> even before anything has persisted that.
    /// </summary>
    public OfferStatus EffectiveStatus(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return HasLapsed(clock) ? OfferStatus.Expired : Status;
    }

    /// <summary>Which side <paramref name="userId"/> is on, or <c>null</c> for anyone else.</summary>
    public OfferParty? PartyOf(string? userId)
    {
        if (string.Equals(BuyerId, userId, StringComparison.Ordinal))
        {
            return OfferParty.Buyer;
        }

        return string.Equals(SellerId, userId, StringComparison.Ordinal) ? OfferParty.Seller : null;
    }

    /// <summary>
    /// Creates an offer, applying both the platform floor and the seller's own
    /// auto-decline threshold.
    /// </summary>
    /// <param name="autoDeclineBelowPercent">
    /// The seller's threshold from the sell form, or <c>null</c> when they set none.
    /// </param>
    /// <param name="buyerUsername">The buyer's public handle, kept so the seller can see who it is from.</param>
    /// <param name="sellerUsername">The seller's public handle, kept so the buyer can see who answered.</param>
    public static Offer Create(
        string listingId,
        string buyerId,
        string sellerId,
        Money amount,
        Money askingPrice,
        string? message,
        decimal? autoDeclineBelowPercent,
        IClock clock,
        string? buyerUsername = null,
        string? sellerUsername = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.Equals(buyerId, sellerId, StringComparison.Ordinal))
        {
            throw DomainException.For("offer.self_offer", "You cannot make an offer on your own listing.");
        }

        if (amount >= askingPrice)
        {
            throw DomainException.For(
                "offer.not_below_asking",
                "An offer must be below the asking price. Use Buy now to pay the asking price.");
        }

        // Compared on the raw rupiah ratio, never on AsPercentageOf: that rounds to two decimals,
        // so 59.999 on 300.000 (19.9997%) read as 20% and slipped past the floor (QA T-04).
        if (amount.IsBelowPercentOf(askingPrice, AbsoluteMinimumPercent))
        {
            throw DomainException.For(
                "offer.too_low",
                $"An offer must be at least {AbsoluteMinimumPercent}% of the asking price.");
        }

        var now = clock.UtcNow;
        var offer = new Offer(
            EntityId.New(now),
            listingId,
            buyerId,
            sellerId,
            amount,
            askingPrice,
            NormaliseMessage(message),
            now)
        {
            BuyerUsername = buyerUsername,
            SellerUsername = sellerUsername,
        };

        // The seller's threshold is applied here rather than left to a background job so
        // the buyer learns immediately, instead of waiting out a 24-hour window that was
        // never going to be answered.
        if (autoDeclineBelowPercent is { } threshold && amount.IsBelowPercentOf(askingPrice, threshold))
        {
            offer.Status = OfferStatus.Declined;
            offer.RespondedAt = now;
        }

        return offer;
    }

    public static Offer Rehydrate(
        string id,
        string listingId,
        string buyerId,
        string sellerId,
        Money amount,
        Money? counterAmount,
        Money askingPrice,
        string? message,
        string? buyerUsername,
        string? sellerUsername,
        string? orderId,
        OfferStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? respondedAt,
        long version) =>
        new(id, listingId, buyerId, sellerId, amount, askingPrice, message, createdAt)
        {
            CounterAmount = counterAmount,
            BuyerUsername = buyerUsername,
            SellerUsername = sellerUsername,
            OrderId = orderId,
            Status = status,
            ExpiresAt = expiresAt,
            RespondedAt = respondedAt,
            Version = version,
        };

    /// <summary>
    /// Accepts the price on the table: the seller on a <c>Pending</c> offer, the buyer on a
    /// <c>Countered</c> one. The buyer may then check out at <see cref="CurrentAmount"/>.
    /// </summary>
    public void Accept(string actingUserId, IClock clock)
    {
        EnsureAwaitingReplyFrom(actingUserId, clock);

        var now = clock.UtcNow;
        Status = OfferStatus.Accepted;
        RespondedAt = now;

        // The awaited thing is now the buyer's payment, which has its own, longer deadline.
        ExpiresAt = now + CheckoutWindow;
        Version++;
    }

    /// <summary>Refuses the price on the table: the seller on <c>Pending</c>, the buyer on <c>Countered</c>.</summary>
    public void Decline(string actingUserId, IClock clock)
    {
        EnsureAwaitingReplyFrom(actingUserId, clock);

        Status = OfferStatus.Declined;
        RespondedAt = clock.UtcNow;
        Version++;
    }

    /// <summary>
    /// The seller proposes a different price on a <c>Pending</c> offer, which restarts the
    /// validity window for the buyer.
    /// </summary>
    /// <remarks>
    /// The counter must be above the buyer's own offer (otherwise it is a worse deal for the
    /// seller than accepting, which they can simply do) and at or below the asking price
    /// recorded when the offer was made (a seller cannot use a counter to charge more than
    /// they listed for). One counter per offer: only a <c>Pending</c> offer is the seller's
    /// move, and a counter makes it <c>Countered</c>, so a second counter is refused as
    /// "not your turn". The counter is final, take it or leave it, which bounds a negotiation
    /// at three moves.
    /// </remarks>
    public void Counter(Money counterAmount, string actingSellerId, IClock clock)
    {
        EnsureAwaitingReplyFrom(actingSellerId, clock);

        if (counterAmount <= Amount)
        {
            throw DomainException.For(
                "offer.counter_not_higher",
                "A counter-offer must be above the buyer's offer. Accept it instead.");
        }

        if (counterAmount > AskingPrice)
        {
            throw DomainException.For(
                "offer.counter_above_asking",
                "A counter-offer cannot exceed the asking price.");
        }

        var now = clock.UtcNow;
        CounterAmount = counterAmount;
        Status = OfferStatus.Countered;
        RespondedAt = now;

        // The clock restarts because the decision has moved to the buyer, who would
        // otherwise inherit whatever remained of their own window.
        ExpiresAt = now + Validity;
        Version++;
    }

    /// <summary>The buyer retracts a <c>Pending</c> or <c>Countered</c> offer.</summary>
    public void Withdraw(string actingBuyerId, IClock clock)
    {
        if (!string.Equals(BuyerId, actingBuyerId, StringComparison.Ordinal))
        {
            throw DomainException.For("offer.forbidden", "Only the buyer can withdraw this offer.");
        }

        if (Status == OfferStatus.Expired || (Status is OfferStatus.Pending or OfferStatus.Countered && HasLapsed(clock)))
        {
            throw DomainException.For("offer.expired", "This offer has expired.");
        }

        if (Status is not (OfferStatus.Pending or OfferStatus.Countered))
        {
            throw DomainException.For("offer.not_withdrawable", $"A {Status} offer cannot be withdrawn.");
        }

        Status = OfferStatus.Withdrawn;
        RespondedAt = clock.UtcNow;
        Version++;
    }

    /// <summary>
    /// Records that the buyer's order was created from this accepted offer, spending it.
    /// </summary>
    /// <remarks>
    /// The repository write that persists this is conditional on the version read, so two
    /// concurrent checkouts of one offer cannot both succeed: the loser's write is refused.
    /// </remarks>
    public void MarkCheckedOut(string actingBuyerId, string orderId, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        if (!string.Equals(BuyerId, actingBuyerId, StringComparison.Ordinal))
        {
            throw DomainException.For("offer.forbidden", "Only the buyer can check out this offer.");
        }

        if (Status == OfferStatus.CheckedOut)
        {
            throw DomainException.For("offer.already_used", "This offer has already been used for an order.");
        }

        if (Status == OfferStatus.Expired || (Status == OfferStatus.Accepted && HasLapsed(clock)))
        {
            throw DomainException.For("offer.expired", "This offer has expired.");
        }

        if (Status != OfferStatus.Accepted)
        {
            throw DomainException.For("offer.not_accepted", $"A {Status} offer cannot be checked out.");
        }

        OrderId = orderId;
        Status = OfferStatus.CheckedOut;
        Version++;
    }

    /// <summary>
    /// Gives a checked-out offer back to the buyer because the order it created was never
    /// paid and has been cancelled or has lapsed. A no-op for an offer that is not checked out.
    /// </summary>
    /// <remarks>
    /// Without this, closing the payment page would burn the deal: the offer is spent, the
    /// order dies after 15 minutes, and the seller has agreed a price nobody can use. The
    /// original <see cref="ExpiresAt"/> still applies, so reopening never extends the deal;
    /// past it the offer is simply expired.
    /// </remarks>
    public void ReopenAfterUnpaidOrder(IClock clock)
    {
        if (Status != OfferStatus.CheckedOut)
        {
            return;
        }

        Status = clock.UtcNow >= ExpiresAt ? OfferStatus.Expired : OfferStatus.Accepted;
        OrderId = null;
        Version++;
    }

    /// <summary>
    /// Marks an unresolved offer whose deadline has passed as expired. Idempotent, and
    /// applied lazily by reads and actions; a future sweep can call it too.
    /// </summary>
    /// <returns><c>true</c> when the offer changed and needs saving.</returns>
    public bool Expire(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!HasLapsed(clock))
        {
            return false;
        }

        Status = OfferStatus.Expired;
        Version++;
        return true;
    }

    private bool HasLapsed(IClock clock) =>
        Status is OfferStatus.Pending or OfferStatus.Countered or OfferStatus.Accepted
        && clock.UtcNow >= ExpiresAt;

    /// <summary>Whose move it is on a <c>Pending</c> or <c>Countered</c> offer.</summary>
    private OfferParty OwnerOfTurn() => Status == OfferStatus.Countered ? OfferParty.Buyer : OfferParty.Seller;

    /// <summary>
    /// Refuses unless the offer is still unresolved, in time, and the actor is the party whose
    /// answer it is waiting for.
    /// </summary>
    /// <remarks>
    /// Order matters: a stranger learns nothing about the state (<c>offer.forbidden</c>), a
    /// settled offer reports that it is settled (so a double-tap is a plain 409, not a 403),
    /// and only then is the turn checked.
    /// </remarks>
    private void EnsureAwaitingReplyFrom(string actingUserId, IClock clock)
    {
        var party = PartyOf(actingUserId)
            ?? throw DomainException.For("offer.forbidden", "Only the buyer or the seller can answer this offer.");

        if (Status == OfferStatus.Expired || (Status is OfferStatus.Pending or OfferStatus.Countered && HasLapsed(clock)))
        {
            throw DomainException.For("offer.expired", "This offer has expired.");
        }

        if (Status is not (OfferStatus.Pending or OfferStatus.Countered))
        {
            throw DomainException.For("offer.already_resolved", $"This offer is already {Status}.");
        }

        if (party != OwnerOfTurn())
        {
            throw DomainException.For(
                "offer.not_your_turn.forbidden",
                party == OfferParty.Buyer
                    ? "The seller has not answered this offer yet."
                    : "The buyer has not answered your counter-offer yet.");
        }
    }

    private static string? NormaliseMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        const int maxLength = 500;
        var trimmed = message.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
