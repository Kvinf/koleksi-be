using Koleksi.Domain.Common;

namespace Koleksi.Domain.Offers;

/// <summary>Where an offer stands. Drives the status badges on the buyer's "My offers" tab.</summary>
public enum OfferStatus
{
    /// <summary>Awaiting the seller. Still within its validity window.</summary>
    Pending = 0,

    /// <summary>The seller proposed a different price; the ball is back with the buyer.</summary>
    Countered = 1,

    /// <summary>Agreed. The buyer may now check out at the agreed price.</summary>
    Accepted = 2,

    /// <summary>Refused by the seller, or auto-declined below their threshold.</summary>
    Declined = 3,

    /// <summary>The validity window closed with no response.</summary>
    Expired = 4,

    /// <summary>Retracted by the buyer before the seller responded.</summary>
    Withdrawn = 5,
}

/// <summary>
/// A buyer's proposal to buy a <c>nego</c> listing below its asking price, and the
/// seller's response.
/// </summary>
/// <remarks>
/// <para>
/// Offers expire. The design states a 24-hour validity on the offer modal, and the
/// deadline is load-bearing rather than cosmetic: an offer that never expires is a
/// free option on a one-of-one item, letting a buyer tie up a seller's listing
/// indefinitely at no cost.
/// </para>
/// <para>
/// An accepted offer is a price commitment, not a purchase. The buyer still checks out,
/// and the order carries the agreed price as its unit price.
/// </para>
/// </remarks>
public sealed class Offer
{
    /// <summary>How long an offer stays open, matching the modal's stated 24 hours.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromHours(24);

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

    /// <summary>The amount currently on the table — the counter amount once countered.</summary>
    public Money Amount { get; private set; }

    /// <summary>The listing's asking price when the offer was made, for the "% of asking" hint.</summary>
    public Money AskingPrice { get; }

    /// <summary>Optional note from the buyer. Length-capped and rendered as plain text.</summary>
    public string? Message { get; }

    public OfferStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RespondedAt { get; private set; }
    public long Version { get; private set; }

    /// <summary>This offer as a percentage of asking. The modal turns this accent above 85%.</summary>
    public decimal PercentOfAsking => Amount.AsPercentageOf(AskingPrice);

    /// <summary>Whether the offer can still be acted on right now.</summary>
    public bool IsActionable(IClock clock) =>
        Status is OfferStatus.Pending or OfferStatus.Countered && clock.UtcNow < ExpiresAt;

    /// <summary>
    /// Creates an offer, applying both the platform floor and the seller's own
    /// auto-decline threshold.
    /// </summary>
    /// <param name="autoDeclineBelowPercent">
    /// The seller's threshold from the sell form, or <c>null</c> when they set none.
    /// </param>
    public static Offer Create(
        string listingId,
        string buyerId,
        string sellerId,
        Money amount,
        Money askingPrice,
        string? message,
        decimal? autoDeclineBelowPercent,
        IClock clock)
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

        var percent = amount.AsPercentageOf(askingPrice);
        if (percent < AbsoluteMinimumPercent)
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
            now);

        // The seller's threshold is applied here rather than left to a background job so
        // the buyer learns immediately, instead of waiting out a 24-hour window that was
        // never going to be answered.
        if (autoDeclineBelowPercent is { } threshold && percent < threshold)
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
        Money askingPrice,
        string? message,
        OfferStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? respondedAt,
        long version) =>
        new(id, listingId, buyerId, sellerId, amount, askingPrice, message, createdAt)
        {
            Status = status,
            ExpiresAt = expiresAt,
            RespondedAt = respondedAt,
            Version = version,
        };

    /// <summary>Seller accepts. The buyer may now check out at <see cref="Amount"/>.</summary>
    public void Accept(string actingSellerId, IClock clock)
    {
        EnsureSeller(actingSellerId);
        EnsureActionable(clock);

        Status = OfferStatus.Accepted;
        RespondedAt = clock.UtcNow;
        Version++;
    }

    /// <summary>Seller declines.</summary>
    public void Decline(string actingSellerId, IClock clock)
    {
        EnsureSeller(actingSellerId);
        EnsureActionable(clock);

        Status = OfferStatus.Declined;
        RespondedAt = clock.UtcNow;
        Version++;
    }

    /// <summary>
    /// Seller proposes a different price, which restarts the validity window for the buyer.
    /// </summary>
    public void Counter(Money counterAmount, string actingSellerId, IClock clock)
    {
        EnsureSeller(actingSellerId);
        EnsureActionable(clock);

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
        Amount = counterAmount;
        Status = OfferStatus.Countered;
        RespondedAt = now;

        // The clock restarts because the decision has moved to the buyer, who would
        // otherwise inherit whatever remained of their own window.
        ExpiresAt = now + Validity;
        Version++;
    }

    /// <summary>Buyer retracts before the seller responds.</summary>
    public void Withdraw(string actingBuyerId, IClock clock)
    {
        if (!string.Equals(BuyerId, actingBuyerId, StringComparison.Ordinal))
        {
            throw DomainException.For("offer.forbidden", "Only the buyer can withdraw this offer.");
        }

        if (Status != OfferStatus.Pending)
        {
            throw DomainException.For("offer.not_withdrawable", $"A {Status} offer cannot be withdrawn.");
        }

        Status = OfferStatus.Withdrawn;
        RespondedAt = clock.UtcNow;
        Version++;
    }

    /// <summary>Marks a lapsed offer expired. Driven by a sweep, and idempotent.</summary>
    public void Expire(IClock clock)
    {
        if (Status is not (OfferStatus.Pending or OfferStatus.Countered) || clock.UtcNow < ExpiresAt)
        {
            return;
        }

        Status = OfferStatus.Expired;
        Version++;
    }

    private void EnsureSeller(string userId)
    {
        if (!string.Equals(SellerId, userId, StringComparison.Ordinal))
        {
            throw DomainException.For("offer.forbidden", "Only the seller can respond to this offer.");
        }
    }

    private void EnsureActionable(IClock clock)
    {
        if (clock.UtcNow >= ExpiresAt)
        {
            throw DomainException.For("offer.expired", "This offer has expired.");
        }

        if (Status is not (OfferStatus.Pending or OfferStatus.Countered))
        {
            throw DomainException.For("offer.already_resolved", $"This offer is already {Status}.");
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
