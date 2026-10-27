using Koleksi.Domain.Common;
using Koleksi.Domain.Messaging;
using Koleksi.Domain.Orders;

namespace Koleksi.Domain.Reviews;

/// <summary>A buyer's rating and comment on the seller they bought from, for one order.</summary>
/// <remarks>
/// <para>
/// A review is evidence of a completed purchase, not an opinion anyone may leave: only the buyer
/// of the order, only once, and only after the order reached the point where the buyer actually
/// has the item. That is what makes the number on a storefront mean something.
/// </para>
/// <para>
/// Immutable once written. There is no edit and no delete here: a seller who could have a review
/// removed would hold the rating hostage, and a buyer who could rewrite one after a refund would
/// hold the seller's. The counters beside the reviews are append-only for the same reason.
/// </para>
/// </remarks>
public sealed class Review
{
    public const int MinRating = 1;

    public const int MaxRating = 5;

    /// <summary>Same ceiling as a chat message, and cleaned by the same rules.</summary>
    public const int MaxTextLength = ChatMessage.MaxBodyLength;

    private Review(
        string id,
        string orderId,
        string listingId,
        string sellerId,
        string buyerId,
        int rating,
        string? text,
        bool isAnonymous,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrderId = orderId;
        ListingId = listingId;
        SellerId = sellerId;
        BuyerId = buyerId;
        Rating = rating;
        Text = text;
        IsAnonymous = isAnonymous;
        CreatedAt = createdAt;
    }

    /// <summary>Time-ordered, so a seller's reviews sort newest-first on the id alone.</summary>
    public string Id { get; }

    public string OrderId { get; }

    /// <summary>The first item on the order, which is what the review is shown against.</summary>
    public string ListingId { get; }

    public string SellerId { get; }

    /// <summary>The reviewer's Cognito subject. Server-side only; never in a response.</summary>
    public string BuyerId { get; }

    public int Rating { get; }

    public string? Text { get; }

    /// <summary>Whether the buyer chose to hide their name. The review still counts.</summary>
    public bool IsAnonymous { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Whether an order in this escrow state has put the item in the buyer's hands.
    /// </summary>
    /// <remarks>
    /// Judged on the state alone, not on how an order gets there, so the rule stays right when
    /// the path to <see cref="EscrowState.Delivered"/> changes. A disputed order is not
    /// reviewable until the dispute resolves into <see cref="EscrowState.Released"/>.
    /// </remarks>
    public static bool IsReviewableState(EscrowState state) =>
        state is EscrowState.Delivered or EscrowState.Released;

    /// <summary>Whether <paramref name="userId"/> may review <paramref name="order"/> right now (ignoring "already reviewed").</summary>
    public static bool CanReview(Order order, string userId)
    {
        ArgumentNullException.ThrowIfNull(order);

        return !string.IsNullOrEmpty(userId)
            && string.Equals(order.BuyerId, userId, StringComparison.Ordinal)
            && !string.Equals(order.SellerId, userId, StringComparison.Ordinal)
            && IsReviewableState(order.EscrowState);
    }

    /// <summary>Creates a review, applying every rule.</summary>
    /// <exception cref="DomainException">
    /// <c>review.self_review</c>, <c>review.forbidden</c>, <c>review.not_eligible</c>,
    /// <c>review.invalid_rating</c>, or <c>review.text_too_long</c>.
    /// </exception>
    public static Review Create(
        Order order, string reviewerId, int rating, string? text, bool isAnonymous, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerId);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.Equals(order.SellerId, reviewerId, StringComparison.Ordinal))
        {
            throw DomainException.For("review.self_review", "A seller cannot review their own sale.");
        }

        if (!string.Equals(order.BuyerId, reviewerId, StringComparison.Ordinal))
        {
            throw DomainException.For("review.forbidden", "Only the buyer of an order can review it.");
        }

        if (!IsReviewableState(order.EscrowState))
        {
            throw DomainException.For(
                "review.not_eligible", "An order can be reviewed once the item has been delivered.");
        }

        if (rating is < MinRating or > MaxRating)
        {
            throw DomainException.For("review.invalid_rating", "The rating must be between 1 and 5.");
        }

        var cleaned = ChatMessage.Normalise(text);
        if (cleaned.Length > MaxTextLength)
        {
            throw DomainException.For(
                "review.text_too_long", $"A review can be at most {MaxTextLength} characters.");
        }

        var now = clock.UtcNow;
        return new Review(
            EntityId.New(now),
            order.Id,
            order.Lines.Count > 0 ? order.Lines[0].ListingId : string.Empty,
            order.SellerId,
            order.BuyerId,
            rating,
            cleaned.Length == 0 ? null : cleaned,
            isAnonymous,
            now);
    }

    public static Review Rehydrate(
        string id,
        string orderId,
        string listingId,
        string sellerId,
        string buyerId,
        int rating,
        string? text,
        bool isAnonymous,
        DateTimeOffset createdAt) =>
        new(id, orderId, listingId, sellerId, buyerId, rating, text, isAnonymous, createdAt);
}

/// <summary>A seller's running rating, kept as counters so reading it never means summing reviews.</summary>
/// <param name="Count">How many reviews.</param>
/// <param name="Sum">The sum of every rating.</param>
/// <param name="Histogram">Reviews per star: index 0 is one star, index 4 is five.</param>
public sealed record SellerRatingStats(long Count, long Sum, IReadOnlyList<long> Histogram)
{
    public static SellerRatingStats Empty { get; } = new(0, 0, [0, 0, 0, 0, 0]);

    /// <summary>The mean to one decimal place, or <c>null</c> while there are no reviews.</summary>
    /// <remarks>
    /// Null rather than zero: a seller nobody has reviewed is not a seller rated 0.0, and the
    /// storefront must be able to say "no reviews yet" instead of a number that reads as a verdict.
    /// </remarks>
    public decimal? Average => Count == 0
        ? null
        : Math.Round((decimal)Sum / Count, 1, MidpointRounding.AwayFromZero);
}
