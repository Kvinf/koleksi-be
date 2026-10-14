using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Auctions;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Maps an <see cref="Auction"/> to and from its stored item.</summary>
internal static class AuctionItem
{
    public const string TypeName = "Auction";

    private const string ListingIdAttr = "listingId";
    private const string SellerIdAttr = "sellerId";
    private const string StartingBidAttr = "startingBid";
    private const string ReservePriceAttr = "reservePrice";
    private const string BuyoutPriceAttr = "buyoutPrice";
    private const string CurrentPriceAttr = "currentPrice";
    private const string BidCountAttr = "bidCount";
    private const string StartsAtAttr = "startsAt";
    private const string EndsAtAttr = "endsAt";
    private const string StatusAttr = "status";
    private const string LeadingBidderIdAttr = "leadingBidderId";
    private const string LeadingBidderUsernameAttr = "leadingBidderUsername";
    private const string VersionAttr = "version";
    private const string ClosedAtAttr = "closedAt";
    private const string WinnerPaymentResolvedAtAttr = "winnerPaymentResolvedAt";

    /// <summary>The due-index state for an ended auction whose winner has not yet paid or lapsed.</summary>
    public const string WinnerPaymentState = "WinnerPay";

    public static string VersionAttribute => VersionAttr;

    public static Dictionary<string, AttributeValue> ToItem(Auction auction)
    {
        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(auction.ListingId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.AuctionSk()),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [ListingIdAttr] = Attr.S(auction.ListingId),
            [SellerIdAttr] = Attr.S(auction.SellerId),
            [StartingBidAttr] = Attr.MoneyValue(auction.StartingBid),
            [ReservePriceAttr] = Attr.MoneyValueOrNull(auction.ReservePrice),
            [BuyoutPriceAttr] = Attr.MoneyValueOrNull(auction.BuyoutPrice),
            [CurrentPriceAttr] = Attr.MoneyValue(auction.CurrentPrice),
            [BidCountAttr] = Attr.N(auction.BidCount),
            [StartsAtAttr] = Attr.Instant(auction.StartsAt),
            [EndsAtAttr] = Attr.Instant(auction.EndsAt),
            [StatusAttr] = Attr.EnumValue(auction.Status),
            [LeadingBidderIdAttr] = Attr.SOrNull(auction.LeadingBidderId),
            [LeadingBidderUsernameAttr] = Attr.SOrNull(auction.LeadingBidderUsername),

            [VersionAttr] = Attr.N(auction.Version),
            [ClosedAtAttr] = Attr.InstantOrNull(auction.ClosedAt),
            [WinnerPaymentResolvedAtAttr] = Attr.InstantOrNull(auction.WinnerPaymentResolvedAt),
        };

        // The close-time index is sparse: only a live auction carries the key attributes,
        // so closing one removes it from the index rather than leaving a tombstone the
        // sweep has to read and skip forever.
        if (auction.Status == AuctionStatus.Live)
        {
            item[DynamoTable.AuctionEndPk] = Attr.S(
                DynamoTable.AuctionEndPkValue(nameof(AuctionStatus.Live), DynamoTable.ShardOf(auction.ListingId)));
            item[DynamoTable.AuctionEndSk] = Attr.S(
                DynamoTable.AuctionEndSkValue(auction.EndsAt, auction.ListingId));
        }
        else if (auction.Status == AuctionStatus.Ended
            && auction.WinnerPaymentResolvedAt is null
            && auction.PaymentDueAt is { } paymentDue)
        {
            // An ended auction with a winner is due once the winner's thirty minutes are up, until it is
            // resolved (paid, or the lapse recorded), which writes the item without these attributes.
            item[DynamoTable.AuctionEndPk] = Attr.S(
                DynamoTable.AuctionEndPkValue(WinnerPaymentState, DynamoTable.ShardOf(auction.ListingId)));
            item[DynamoTable.AuctionEndSk] = Attr.S(DynamoTable.AuctionEndSkValue(paymentDue, auction.ListingId));
        }

        return item;
    }

    public static Auction ToDomain(Dictionary<string, AttributeValue> item) => Auction.Rehydrate(
        listingId: item.GetString(ListingIdAttr),
        sellerId: item.GetString(SellerIdAttr),
        startingBid: item.GetMoney(StartingBidAttr),
        currentPrice: item.GetMoney(CurrentPriceAttr),
        bidCount: item.GetInt(BidCountAttr),
        startsAt: item.GetInstant(StartsAtAttr),
        endsAt: item.GetInstant(EndsAtAttr),
        status: item.GetEnum<AuctionStatus>(StatusAttr),
        leadingBidderId: item.GetStringOrNull(LeadingBidderIdAttr),
        leadingBidderUsername: item.GetStringOrNull(LeadingBidderUsernameAttr),
        version: item.GetLongOrDefault(VersionAttr),
        reservePrice: item.GetMoneyOrNull(ReservePriceAttr),
        buyoutPrice: item.GetMoneyOrNull(BuyoutPriceAttr),
        closedAt: item.GetInstantOrNull(ClosedAtAttr),
        winnerPaymentResolvedAt: item.GetInstantOrNull(WinnerPaymentResolvedAtAttr));
}

/// <summary>Maps a <see cref="Bid"/> to and from its stored item.</summary>
/// <remarks>
/// Bids are append-only. There is no update path and no delete path: the history is the
/// record a disputed auction is settled from, and a row that can be rewritten is not a
/// record. Amending one means appending a correction, not editing the original.
/// </remarks>
internal static class BidItem
{
    public const string TypeName = "Bid";

    private const string IdAttr = "id";
    private const string AuctionIdAttr = "auctionId";
    private const string BidderIdAttr = "bidderId";
    private const string BidderUsernameAttr = "bidderUsername";
    private const string AmountAttr = "amount";
    private const string PlacedAtAttr = "placedAt";

    public static Dictionary<string, AttributeValue> ToItem(Bid bid) =>
        new(StringComparer.Ordinal)
        {
            // The auction shares the listing's id, so a bid lands in the same partition as
            // the auction it changes — which is what lets the two be written atomically.
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(bid.AuctionId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.BidSk(bid.Id)),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(bid.Id),
            [AuctionIdAttr] = Attr.S(bid.AuctionId),
            [BidderIdAttr] = Attr.S(bid.BidderId),
            [BidderUsernameAttr] = Attr.S(bid.BidderUsername),
            [AmountAttr] = Attr.MoneyValue(bid.Amount),
            [PlacedAtAttr] = Attr.Instant(bid.PlacedAt),
        };

    public static Bid ToDomain(Dictionary<string, AttributeValue> item) => new(
        Id: item.GetString(IdAttr),
        AuctionId: item.GetString(AuctionIdAttr),
        BidderId: item.GetString(BidderIdAttr),
        BidderUsername: item.GetString(BidderUsernameAttr),
        Amount: item.GetMoney(AmountAttr),
        PlacedAt: item.GetInstant(PlacedAtAttr));
}
