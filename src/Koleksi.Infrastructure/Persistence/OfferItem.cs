using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Maps an <see cref="Offer"/> to and from its stored item.</summary>
internal static class OfferItem
{
    public const string TypeName = "Offer";

    private const string IdAttr = "id";
    private const string ListingIdAttr = "listingId";
    private const string BuyerIdAttr = "buyerId";
    private const string SellerIdAttr = "sellerId";
    private const string AmountAttr = "amount";
    private const string CounterAmountAttr = "counterAmount";
    private const string AskingPriceAttr = "askingPrice";
    private const string MessageAttr = "message";
    private const string BuyerUsernameAttr = "buyerUsername";
    private const string SellerUsernameAttr = "sellerUsername";
    private const string OrderIdAttr = "orderId";
    private const string StatusAttr = "status";
    private const string CreatedAtAttr = "createdAt";
    private const string ExpiresAtAttr = "expiresAt";
    private const string RespondedAtAttr = "respondedAt";
    private const string VersionAttr = "version";

    public static string VersionAttribute => VersionAttr;

    /// <summary>Whether an offer in this status still needs the active-offer pointer row.</summary>
    /// <remarks>
    /// <c>Accepted</c> counts: an accepted-but-unpaid offer is still the buyer's open deal on
    /// that item, and letting them stack a second offer beside it would defeat the pointer. So does
    /// <c>CheckedOut</c>: its order may die unpaid, which reopens the deal as <c>Accepted</c>, and by
    /// then the buyer must not hold a second open offer on the same listing (D-08).
    /// </remarks>
    public static bool IsActiveStatus(OfferStatus status) =>
        status is OfferStatus.Pending or OfferStatus.Countered or OfferStatus.Accepted or OfferStatus.CheckedOut;

    public static Dictionary<string, AttributeValue> ToItem(Offer offer)
    {
        var createdAt = DynamoTable.FormatInstant(offer.CreatedAt);

        return new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.OfferPk(offer.Id)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.OfferMetaSk()),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(offer.Id),
            [ListingIdAttr] = Attr.S(offer.ListingId),
            [BuyerIdAttr] = Attr.S(offer.BuyerId),
            [SellerIdAttr] = Attr.S(offer.SellerId),
            [AmountAttr] = Attr.MoneyValue(offer.Amount),
            [CounterAmountAttr] = Attr.MoneyValueOrNull(offer.CounterAmount),
            [AskingPriceAttr] = Attr.MoneyValue(offer.AskingPrice),
            [MessageAttr] = Attr.SOrNull(offer.Message),
            [BuyerUsernameAttr] = Attr.SOrNull(offer.BuyerUsername),
            [SellerUsernameAttr] = Attr.SOrNull(offer.SellerUsername),
            [OrderIdAttr] = Attr.SOrNull(offer.OrderId),
            [StatusAttr] = Attr.EnumValue(offer.Status),
            [CreatedAtAttr] = Attr.S(createdAt),
            [ExpiresAtAttr] = Attr.Instant(offer.ExpiresAt),
            [RespondedAtAttr] = Attr.InstantOrNull(offer.RespondedAt),
            [VersionAttr] = Attr.N(offer.Version),

            [DynamoTable.SellerPk] = Attr.S(DynamoTable.SellerPkValue(offer.SellerId)),
            [DynamoTable.SellerSk] = Attr.S(
                DynamoTable.SellerSkValue(DynamoTable.OfferIndexPrefix(), createdAt, offer.Id)),
            [DynamoTable.BuyerPk] = Attr.S(DynamoTable.BuyerPkValue(offer.BuyerId)),
            [DynamoTable.BuyerSk] = Attr.S(
                DynamoTable.BuyerSkValue(DynamoTable.OfferIndexPrefix(), createdAt, offer.Id)),
        };
    }

    /// <remarks>
    /// Rows written before counters were kept apart lack the newer attributes and read as an
    /// offer with no counter and no names. One exception: those rows overwrote <c>amount</c>
    /// with the counter, so a <c>Countered</c> row without a <c>counterAmount</c> reads its
    /// stored amount as the counter (the buyer's original amount is not recoverable).
    /// </remarks>
    public static Offer ToDomain(Dictionary<string, AttributeValue> item)
    {
        var status = item.GetEnum<OfferStatus>(StatusAttr);
        var amount = item.GetMoney(AmountAttr);
        var counterAmount = item.GetMoneyOrNull(CounterAmountAttr);
        if (counterAmount is null && status == OfferStatus.Countered)
        {
            counterAmount = amount;
        }

        return Offer.Rehydrate(
            id: item.GetString(IdAttr),
            listingId: item.GetString(ListingIdAttr),
            buyerId: item.GetString(BuyerIdAttr),
            sellerId: item.GetString(SellerIdAttr),
            amount: amount,
            counterAmount: counterAmount,
            askingPrice: item.GetMoney(AskingPriceAttr),
            message: item.GetStringOrNull(MessageAttr),
            buyerUsername: item.GetStringOrNull(BuyerUsernameAttr),
            sellerUsername: item.GetStringOrNull(SellerUsernameAttr),
            orderId: item.GetStringOrNull(OrderIdAttr),
            status: status,
            createdAt: item.GetInstant(CreatedAtAttr),
            expiresAt: item.GetInstant(ExpiresAtAttr),
            respondedAt: item.GetInstantOrNull(RespondedAtAttr),
            version: item.GetLongOrDefault(VersionAttr));
    }
}

/// <summary>
/// The pointer row in a listing's own partition that answers "does this buyer already
/// have an open offer here" — see <see cref="DynamoTable.OfferActiveSk"/>.
/// </summary>
internal static class OfferActivePointerItem
{
    public const string TypeName = "OfferActivePointer";

    private const string OfferIdAttr = "offerId";

    /// <summary>The attribute holding the pointed-to offer's id, for condition expressions.</summary>
    public const string OfferIdAttribute = OfferIdAttr;

    public static Dictionary<string, AttributeValue> ToItem(string listingId, string buyerId, string offerId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listingId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.OfferActiveSk(buyerId)),
            [DynamoTable.EntityType] = Attr.S(TypeName),
            [OfferIdAttr] = Attr.S(offerId),
        };

    public static string GetOfferId(Dictionary<string, AttributeValue> item) => item.GetString(OfferIdAttr);
}
