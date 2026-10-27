using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Reviews;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Maps a <see cref="Review"/> to and from its two stored copies, and the seller counters.</summary>
/// <remarks>
/// A review is stored twice on purpose: under its order (the one-per-order guard, and the buyer's
/// own lookup) and under its seller (the newest-first feed). Neither copy carries a GSI key, so
/// neither can appear in an order, offer or conversation list.
/// </remarks>
internal static class ReviewItem
{
    public const string TypeName = "Review";

    public const string StatsTypeName = "ReviewStats";

    public const string CountAttribute = "ratingCount";

    public const string SumAttribute = "ratingSum";

    private const string IdAttr = "id";
    private const string OrderIdAttr = "orderId";
    private const string ListingIdAttr = "listingId";
    private const string SellerIdAttr = "sellerId";
    private const string BuyerIdAttr = "buyerId";
    private const string RatingAttr = "rating";
    private const string TextAttr = "text";
    private const string AnonymousAttr = "anonymous";
    private const string CreatedAtAttr = "createdAt";

    /// <summary>Counter attribute for one star level (1 to 5).</summary>
    public static string StarAttribute(int stars) => $"star{stars}";

    public static Dictionary<string, AttributeValue> ToOrderItem(Review review) =>
        WithBody(review, DynamoTable.OrderReviewPk(review.OrderId), DynamoTable.OrderReviewSk());

    public static Dictionary<string, AttributeValue> ToFeedItem(Review review) =>
        WithBody(review, DynamoTable.SellerReviewsPk(review.SellerId), DynamoTable.SellerReviewSk(review.Id));

    public static Review ToDomain(Dictionary<string, AttributeValue> item) =>
        Review.Rehydrate(
            id: item.GetString(IdAttr),
            orderId: item.GetString(OrderIdAttr),
            listingId: item.GetStringOrNull(ListingIdAttr) ?? string.Empty,
            sellerId: item.GetString(SellerIdAttr),
            buyerId: item.GetString(BuyerIdAttr),
            rating: item.GetInt(RatingAttr),
            text: item.GetStringOrNull(TextAttr),
            isAnonymous: item.GetBool(AnonymousAttr),
            createdAt: item.GetInstant(CreatedAtAttr));

    public static SellerRatingStats ToStats(Dictionary<string, AttributeValue> item)
    {
        var histogram = new long[5];
        for (var star = 1; star <= 5; star++)
        {
            histogram[star - 1] = item.GetLongOrDefault(StarAttribute(star));
        }

        return new SellerRatingStats(item.GetLongOrDefault(CountAttribute), item.GetLongOrDefault(SumAttribute), histogram);
    }

    private static Dictionary<string, AttributeValue> WithBody(Review review, string pk, string sk) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(pk),
            [DynamoTable.Sk] = Attr.S(sk),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(review.Id),
            [OrderIdAttr] = Attr.S(review.OrderId),
            [ListingIdAttr] = Attr.SOrNull(review.ListingId),
            [SellerIdAttr] = Attr.S(review.SellerId),
            [BuyerIdAttr] = Attr.S(review.BuyerId),
            [RatingAttr] = Attr.N(review.Rating),
            [TextAttr] = Attr.SOrNull(review.Text),
            [AnonymousAttr] = Attr.Bool(review.IsAnonymous),
            [CreatedAtAttr] = Attr.Instant(review.CreatedAt),
        };
}
