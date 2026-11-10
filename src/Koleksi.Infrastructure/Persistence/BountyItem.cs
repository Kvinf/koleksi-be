using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Common;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Maps a <see cref="Bounty"/> to and from its stored item.</summary>
internal static class BountyItem
{
    public const string TypeName = "Bounty";

    private const string IdAttr = "id";
    private const string BuyerIdAttr = "buyerId";
    private const string BuyerUsernameAttr = "buyerUsername";
    private const string TitleAttr = "title";
    private const string CategoryIdAttr = "categoryId";
    private const string FacetsAttr = "facets";
    private const string DescriptionAttr = "description";
    private const string ConditionsAttr = "conditions";
    private const string MaxBudgetAttr = "maxBudget";
    private const string ShipToCityAttr = "shipToCity";
    private const string PhotoKeysAttr = "photoKeys";
    private const string CreatedAtAttr = "createdAt";
    private const string ExpiresAtAttr = "expiresAt";
    private const string StatusAttr = "status";
    private const string AcceptedProposalIdAttr = "acceptedProposalId";
    private const string DealDueAtAttr = "dealDueAt";
    private const string RemovedReasonAttr = "removedReason";
    private const string VersionAttr = "version";

    /// <summary>Maintained by an atomic <c>ADD</c>, so it is written when the item is created and never again by an update.</summary>
    public const string ProposalCountAttr = "proposalCount";
    public const string StatusAttrName = StatusAttr;
    public const string AcceptedProposalIdAttrName = AcceptedProposalIdAttr;

    public static string VersionAttribute => VersionAttr;

    /// <summary>The index attributes a Dicari carries only while it is on the board or waiting on a clock.</summary>
    public static IReadOnlyList<string> SparseIndexAttributes { get; } =
    [
        DynamoTable.BrowseNewestPk, DynamoTable.BrowseNewestSk, DynamoTable.AuctionEndPk, DynamoTable.AuctionEndSk,
    ];

    public static Dictionary<string, AttributeValue> ToItem(Bounty bounty)
    {
        var createdAt = DynamoTable.FormatInstant(bounty.CreatedAt);

        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(bounty.Id)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.BountyMetaSk()),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(bounty.Id),
            [BuyerIdAttr] = Attr.S(bounty.BuyerId),
            [BuyerUsernameAttr] = Attr.SOrNull(bounty.BuyerUsername),
            [TitleAttr] = Attr.S(bounty.Title),
            [CategoryIdAttr] = Attr.S(bounty.CategoryId),
            [FacetsAttr] = Attr.StringMap(bounty.Facets),
            [DescriptionAttr] = Attr.SOrNull(bounty.Description),
            [ConditionsAttr] = Attr.ListValue([.. bounty.Conditions.Select(Attr.S)]),
            [MaxBudgetAttr] = Attr.MoneyValue(bounty.MaxBudget),
            [ShipToCityAttr] = Attr.S(bounty.ShipToCity),
            [PhotoKeysAttr] = Attr.ListValue([.. bounty.PhotoKeys.Select(Attr.S)]),
            [CreatedAtAttr] = Attr.S(createdAt),
            [ExpiresAtAttr] = Attr.Instant(bounty.ExpiresAt),
            [StatusAttr] = Attr.EnumValue(bounty.Status),
            [AcceptedProposalIdAttr] = Attr.SOrNull(bounty.AcceptedProposalId),
            [DealDueAtAttr] = Attr.InstantOrNull(bounty.DealDueAt),
            [RemovedReasonAttr] = Attr.SOrNull(bounty.RemovedReason),
            [VersionAttr] = Attr.N(bounty.Version),
            [ProposalCountAttr] = Attr.N(bounty.ProposalCount),

            [DynamoTable.BuyerPk] = Attr.S(DynamoTable.BuyerPkValue(bounty.BuyerId)),
            [DynamoTable.BuyerSk] = Attr.S(
                DynamoTable.BuyerSkValue(DynamoTable.BountyIndexPrefix(), createdAt, bounty.Id)),
        };

        // On the board only while it is open and no deal is in flight. Absent otherwise, like the browse
        // projection: a row that exists but must never be shown is one forgotten filter away from being shown.
        if (IsOnBoard(bounty))
        {
            item[DynamoTable.BrowseNewestPk] = Attr.S(DynamoTable.BountyOpenPkValue(bounty.CategoryId));
            item[DynamoTable.BrowseNewestSk] = Attr.S(DynamoTable.BountyOpenSkValue(bounty.Id));
        }

        if (DueAt(bounty) is { } due)
        {
            item[DynamoTable.AuctionEndPk] = Attr.S(DynamoTable.BountyDuePkValue(DynamoTable.ShardOf(bounty.Id)));
            item[DynamoTable.AuctionEndSk] = Attr.S(DynamoTable.BountyDueSkValue(due, bounty.Id));
        }

        return item;
    }

    /// <summary>Whether the Dicari belongs on the board's index right now.</summary>
    internal static bool IsOnBoard(Bounty bounty) =>
        bounty.Status == BountyStatus.Open && bounty.AcceptedProposalId is null;

    /// <summary>The instant the sweep should look at an open Dicari, or <c>null</c> when no clock runs on it.</summary>
    internal static DateTimeOffset? DueAt(Bounty bounty) =>
        bounty.Status != BountyStatus.Open ? null : bounty.DealDueAt ?? bounty.ExpiresAt;

    public static Bounty ToDomain(Dictionary<string, AttributeValue> item) =>
        Bounty.Rehydrate(
            id: item.GetString(IdAttr),
            buyerId: item.GetString(BuyerIdAttr),
            buyerUsername: item.GetStringOrNull(BuyerUsernameAttr),
            title: item.GetString(TitleAttr),
            categoryId: item.GetString(CategoryIdAttr),
            facets: item.GetStringMap(FacetsAttr),
            description: item.GetStringOrNull(DescriptionAttr) ?? string.Empty,
            conditions: item.GetList(ConditionsAttr).Select(entry => entry.S).Where(text => text is not null).Select(text => text!),
            maxBudget: item.GetMoney(MaxBudgetAttr),
            shipToCity: item.GetString(ShipToCityAttr),
            photoKeys: item.GetList(PhotoKeysAttr).Select(entry => entry.S).Where(text => text is not null).Select(text => text!),
            createdAt: item.GetInstant(CreatedAtAttr),
            expiresAt: item.GetInstant(ExpiresAtAttr),
            status: item.GetEnum<BountyStatus>(StatusAttr),
            proposalCount: (int)item.GetLongOrDefault(ProposalCountAttr),
            acceptedProposalId: item.GetStringOrNull(AcceptedProposalIdAttr),
            removedReason: item.GetStringOrNull(RemovedReasonAttr),
            dealDueAt: item.GetInstantOrNull(DealDueAtAttr),
            version: item.GetLongOrDefault(VersionAttr));
}

/// <summary>Maps a <see cref="BountyProposal"/> to and from its stored item.</summary>
internal static class BountyProposalItem
{
    public const string TypeName = "BountyProposal";

    private const string IdAttr = "id";
    private const string BountyIdAttr = "bountyId";
    private const string SellerIdAttr = "sellerId";
    private const string SellerUsernameAttr = "sellerUsername";
    private const string ListingIdAttr = "listingId";
    private const string ProposedPriceAttr = "proposedPrice";
    private const string ListingPriceAttr = "listingPriceAtProposal";
    private const string NoteAttr = "note";
    private const string CreatedAtAttr = "createdAt";
    private const string StatusAttr = "status";
    private const string OfferIdAttr = "offerId";
    private const string LapseReasonAttr = "lapseReason";
    private const string VersionAttr = "version";

    public static string VersionAttribute => VersionAttr;

    public static Dictionary<string, AttributeValue> ToItem(BountyProposal proposal)
    {
        var createdAt = DynamoTable.FormatInstant(proposal.CreatedAt);

        return new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(proposal.BountyId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.BountyProposalSk(proposal.Id)),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(proposal.Id),
            [BountyIdAttr] = Attr.S(proposal.BountyId),
            [SellerIdAttr] = Attr.S(proposal.SellerId),
            [SellerUsernameAttr] = Attr.SOrNull(proposal.SellerUsername),
            [ListingIdAttr] = Attr.S(proposal.ListingId),
            [ProposedPriceAttr] = Attr.MoneyValue(proposal.ProposedPrice),
            [ListingPriceAttr] = Attr.MoneyValue(proposal.ListingPriceAtProposal),
            [NoteAttr] = Attr.SOrNull(proposal.Note),
            [CreatedAtAttr] = Attr.S(createdAt),
            [StatusAttr] = Attr.EnumValue(proposal.Status),
            [OfferIdAttr] = Attr.SOrNull(proposal.OfferId),
            [LapseReasonAttr] = Attr.SOrNull(proposal.LapseReason),
            [VersionAttr] = Attr.N(proposal.Version),

            [DynamoTable.SellerPk] = Attr.S(DynamoTable.SellerPkValue(proposal.SellerId)),
            [DynamoTable.SellerSk] = Attr.S(
                DynamoTable.SellerSkValue(DynamoTable.BountyProposalIndexPrefix(), createdAt, proposal.Id)),
        };
    }

    public static BountyProposal ToDomain(Dictionary<string, AttributeValue> item) =>
        BountyProposal.Rehydrate(
            id: item.GetString(IdAttr),
            bountyId: item.GetString(BountyIdAttr),
            sellerId: item.GetString(SellerIdAttr),
            sellerUsername: item.GetStringOrNull(SellerUsernameAttr),
            listingId: item.GetString(ListingIdAttr),
            proposedPrice: item.GetMoney(ProposedPriceAttr),
            listingPriceAtProposal: item.GetMoney(ListingPriceAttr),
            note: item.GetStringOrNull(NoteAttr),
            createdAt: item.GetInstant(CreatedAtAttr),
            status: item.GetEnum<ProposalStatus>(StatusAttr),
            offerId: item.GetStringOrNull(OfferIdAttr),
            lapseReason: item.GetStringOrNull(LapseReasonAttr),
            version: item.GetLongOrDefault(VersionAttr));
}

/// <summary>The pointer row that holds a seller's one slot on a Dicari.</summary>
internal static class BountyProposerPointerItem
{
    public const string TypeName = "BountyProposer";
    public const string ProposalIdAttribute = "proposalId";

    public static Dictionary<string, AttributeValue> ToItem(string bountyId, string sellerId, string proposalId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(bountyId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.BountyProposerSk(sellerId)),
            [DynamoTable.EntityType] = Attr.S(TypeName),
            [ProposalIdAttribute] = Attr.S(proposalId),
        };
}
