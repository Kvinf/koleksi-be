using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Users;

namespace Koleksi.Application.Features.Bounties;

/// <summary>How the board orders Dicari.</summary>
public enum BountySort
{
    Newest = 0,
    BudgetHigh = 1,
    EndingSoon = 2,
    FewestProposals = 3,
}

/// <summary>
/// A Dicari as a stranger may see it: a narrow projection. No buyer id, no address beyond the city, and nothing about
/// the proposals except how many there are.
/// </summary>
public sealed record BountyCard(
    string Id,
    string Code,
    string Title,
    string CategoryId,
    string CategorySlug,
    IReadOnlyDictionary<string, string> Facets,
    long MaxBudgetRupiah,
    string ShipToCity,
    string BuyerHandle,
    int ProposalCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Status,
    string? PhotoKey);

/// <param name="Count">How many open Dicari the category holds right now.</param>
public sealed record BountyCategoryCount(string CategoryId, string CategorySlug, int Count);

public sealed record BountyBoard(NumberedPage<BountyCard> Page, IReadOnlyList<BountyCategoryCount> Categories);

/// <summary>One of the buyer's own Dicari in their list.</summary>
public sealed record MyBountyRow(BountyCard Card, bool DealInFlight);

/// <summary>A proposal as the buyer (full) or the proposing seller (their own) sees it.</summary>
public sealed record ProposalView(
    string Id,
    string SellerHandle,
    DateTimeOffset SellerMemberSince,
    string ListingId,
    string ListingTitle,
    string? ListingPhotoKey,
    string ListingCategorySlug,
    string ListingCondition,
    string ListingCity,
    long ProposedPriceRupiah,
    long ListingPriceRupiah,
    long ListingPriceAtProposalRupiah,
    bool IsUpsell,
    long AmountOverBudgetRupiah,
    string? Note,
    string Status,
    string? LapseReason,
    DateTimeOffset CreatedAt,
    string? OfferId);

/// <param name="Role">
/// <c>owner</c> (sees every proposal), <c>seller</c> (sees only their own) or <c>public</c> (sees only the count).
/// </param>
public sealed record BountyDetailView(
    BountyCard Card,
    string Description,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> PhotoKeys,
    string Role,
    DateTimeOffset BuyerMemberSince,
    bool BuyerContactVerified,
    IReadOnlyList<ProposalView> Proposals,
    bool CanPropose,
    bool DealInFlight,
    string? AcceptedProposalId);

/// <summary>A seller's own proposal in their dashboard, with the Dicari it is on.</summary>
public sealed record MyProposalRow(ProposalView Proposal, BountyCard Bounty);

/// <summary>
/// Reads Dicari for the board, the dashboard and the detail page. The caller may be anonymous for the public reads.
/// </summary>
/// <remarks>
/// The board is a bounded window, not a full walk: DynamoDB has no offset, so each category contributes at most
/// <see cref="BoardWindow"/> of its newest open Dicari and the sort and page are applied to that. A category with more
/// open Dicari than that shows its newest ones, which is where buyers and sellers look anyway.
/// </remarks>
public sealed class BountyViews(
    IBountyRepository bounties,
    IListingRepository listings,
    ICategoryRepository categories,
    IProfileRepository profiles,
    IPublicProfileRepository publicProfiles,
    BountyDeals deals,
    ICurrentUser currentUser,
    IClock clock)
{
    public const int PageSize = 12;
    public const int BoardWindow = 200;
    public const int MaxPage = 50;
    public const int MaxSuggestions = 3;
    public const int MaxRailRows = 3;
    private const int MaxProposalsShown = 50;
    private const int MyListPageSize = 20;

    public async Task<BountyBoard> ListBoardAsync(string? categorySlug, BountySort sort, int page, CancellationToken ct)
    {
        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);
        var wanted = categorySlug is null
            ? all
            : all.Where(c => string.Equals(c.Slug, categorySlug, StringComparison.Ordinal)).ToList();

        var counts = await Task.WhenAll(all.Select(async c =>
            new BountyCategoryCount(c.Id, c.Slug, await bounties.CountOpenAsync(c.Id, ct).ConfigureAwait(false))))
            .ConfigureAwait(false);

        var open = await ReadWindowsAsync(wanted, BoardWindow, ct).ConfigureAwait(false);
        var ordered = Sort(open, sort);

        var pageNumber = Math.Clamp(page, 1, MaxPage);
        var slice = ordered.Skip((pageNumber - 1) * PageSize).Take(PageSize).ToList();
        var cards = await ToCardsAsync(slice, all, ct).ConfigureAwait(false);

        return new BountyBoard(new NumberedPage<BountyCard>(cards, pageNumber, PageSize, ordered.Count), counts);
    }

    /// <summary>Up to <see cref="MaxSuggestions"/> open Dicari matching a search, and how many match, for the empty-results panel.</summary>
    public async Task<(IReadOnlyList<BountyCard> Cards, int Total)> SuggestAsync(
        string? query, string? categorySlug, CancellationToken ct)
    {
        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);
        var wanted = categorySlug is null
            ? all
            : all.Where(c => string.Equals(c.Slug, categorySlug, StringComparison.Ordinal)).ToList();

        var words = Slug.Words(query);
        var open = await ReadWindowsAsync(wanted, 50, ct).ConfigureAwait(false);
        var matching = open
            .Where(b => words.Count == 0 || words.Any(w => b.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(b => b.Id, StringComparer.Ordinal)
            .ToList();

        var cards = await ToCardsAsync(matching.Take(MaxSuggestions).ToList(), all, ct).ConfigureAwait(false);
        return (cards, matching.Count);
    }

    /// <summary>The caller's own Dicari, newest first.</summary>
    public async Task<(IReadOnlyList<MyBountyRow> Rows, string? NextCursor)> ListMineAsync(string? cursor, CancellationToken ct)
    {
        var buyerId = currentUser.RequireUserId();
        var page = await bounties.ListForBuyerAsync(buyerId, cursor, MyListPageSize, ct).ConfigureAwait(false);
        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);

        var items = new List<Bounty>(page.Items.Count);
        foreach (var bounty in page.Items)
        {
            items.Add(await deals.ApplyExpiryAsync(bounty, ct).ConfigureAwait(false));
        }

        var cards = await ToCardsAsync(items, all, ct).ConfigureAwait(false);
        var rows = cards.Select((card, index) => new MyBountyRow(card, items[index].DealInFlight)).ToList();
        return (rows, page.NextCursor);
    }

    /// <summary>The caller's proposals as a seller, newest first, each with the Dicari it is on.</summary>
    public async Task<(IReadOnlyList<MyProposalRow> Rows, string? NextCursor)> ListMyProposalsAsync(
        string? cursor, CancellationToken ct)
    {
        var sellerId = currentUser.RequireUserId();
        var page = await bounties.ListProposalsForSellerAsync(sellerId, cursor, MyListPageSize, ct).ConfigureAwait(false);
        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);

        var rows = new List<MyProposalRow>(page.Items.Count);
        foreach (var proposal in page.Items)
        {
            var bounty = await bounties.FindAsync(proposal.BountyId, ct).ConfigureAwait(false);
            if (bounty is null)
            {
                continue;
            }

            bounty = await deals.ApplyExpiryAsync(bounty, ct).ConfigureAwait(false);
            var listing = await listings.FindAsync(proposal.ListingId, ct).ConfigureAwait(false);
            var card = (await ToCardsAsync([bounty], all, ct).ConfigureAwait(false))[0];
            rows.Add(new MyProposalRow(await ToProposalViewAsync(proposal, bounty, listing, all, ct).ConfigureAwait(false), card));
        }

        return (rows, page.NextCursor);
    }

    /// <summary>
    /// Open Dicari in the categories the caller has active listings in, for the dashboard rail: at most
    /// <see cref="MaxRailRows"/>, never the caller's own.
    /// </summary>
    public async Task<(IReadOnlyList<BountyCard> Cards, int Total)> MatchesAsync(CancellationToken ct)
    {
        var sellerId = currentUser.RequireUserId();
        var active = await listings.ListBySellerAsync(sellerId, ListingStatus.Active, null, 50, ct).ConfigureAwait(false);
        var categoryIds = active.Items.Select(l => l.CategoryId).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (categoryIds.Count == 0)
        {
            return ([], 0);
        }

        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);
        var open = (await ReadWindowsAsync(all.Where(c => categoryIds.Contains(c.Id)).ToList(), 20, ct).ConfigureAwait(false))
            .Where(b => !string.Equals(b.BuyerId, sellerId, StringComparison.Ordinal))
            .OrderByDescending(b => b.Id, StringComparer.Ordinal)
            .ToList();

        var cards = await ToCardsAsync(open.Take(MaxRailRows).ToList(), all, ct).ConfigureAwait(false);
        return (cards, open.Count);
    }

    /// <summary>
    /// One Dicari for whoever is looking. The owner gets every proposal, a signed-in seller only their own, anyone else
    /// only the count. <c>null</c> when it does not exist, or was removed and the viewer is not the owner.
    /// </summary>
    public async Task<BountyDetailView?> GetAsync(string bountyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bountyId);

        var bounty = await bounties.FindAsync(bountyId, ct).ConfigureAwait(false);
        if (bounty is null)
        {
            return null;
        }

        var viewerId = currentUser.UserId;
        var isOwner = viewerId is not null && string.Equals(bounty.BuyerId, viewerId, StringComparison.Ordinal);
        if (bounty.Status == BountyStatus.Removed && !isOwner)
        {
            return null;
        }

        bounty = await deals.ApplyExpiryAsync(bounty, ct).ConfigureAwait(false);

        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);
        var card = (await ToCardsAsync([bounty], all, ct).ConfigureAwait(false))[0];
        var buyerIdentity = await IdentityAsync(bounty.BuyerId, bounty.CreatedAt, ct).ConfigureAwait(false);
        var buyerProfile = await profiles.FindAsync(bounty.BuyerId, ct).ConfigureAwait(false);
        var contactVerified = buyerProfile is { PhoneVerified: true, Phone.Length: > 0 };

        var proposals = new List<ProposalView>();
        string role;
        if (isOwner)
        {
            role = "owner";
            var stored = (await bounties.ListProposalsAsync(bounty.Id, ct).ConfigureAwait(false))
                .OrderByDescending(p => p.Id, StringComparer.Ordinal)
                .Take(MaxProposalsShown)
                .ToList();
            foreach (var proposal in stored)
            {
                proposals.Add(await ViewForOwnerAsync(proposal, bounty, all, ct).ConfigureAwait(false));
            }
        }
        else if (viewerId is not null)
        {
            role = "seller";
            if (await bounties.FindLiveProposalBySellerAsync(bounty.Id, viewerId, ct).ConfigureAwait(false) is { } own)
            {
                var listing = await listings.FindAsync(own.ListingId, ct).ConfigureAwait(false);
                proposals.Add(await ToProposalViewAsync(own, bounty, listing, all, ct).ConfigureAwait(false));
            }
        }
        else
        {
            role = "public";
        }

        var canPropose = role == "seller" && bounty.AcceptsProposals(clock) && proposals.Count == 0;

        return new BountyDetailView(
            card,
            bounty.Description,
            bounty.Conditions,
            bounty.PhotoKeys,
            role,
            buyerIdentity.MemberSince,
            contactVerified,
            proposals,
            canPropose,
            bounty.DealInFlight,
            isOwner ? bounty.AcceptedProposalId : null);
    }

    /// <summary>A pending proposal whose listing can no longer back it is lapsed on the spot, then shown as lapsed.</summary>
    private async Task<ProposalView> ViewForOwnerAsync(
        BountyProposal proposal, Bounty bounty, IReadOnlyList<Category> all, CancellationToken ct)
    {
        var listing = await listings.FindAsync(proposal.ListingId, ct).ConfigureAwait(false);
        if (proposal.Status == ProposalStatus.Pending && BountyListings.WhyGone(bounty, listing, proposal) is not null)
        {
            await deals.LapseBecauseListingGoneAsync(proposal, ct).ConfigureAwait(false);
        }

        return await ToProposalViewAsync(proposal, bounty, listing, all, ct).ConfigureAwait(false);
    }

    private async Task<ProposalView> ToProposalViewAsync(
        BountyProposal proposal, Bounty bounty, Listing? listing, IReadOnlyList<Category> all, CancellationToken ct)
    {
        var seller = await IdentityAsync(proposal.SellerId, proposal.CreatedAt, ct).ConfigureAwait(false);
        var slug = all.FirstOrDefault(c => string.Equals(c.Id, listing?.CategoryId ?? bounty.CategoryId, StringComparison.Ordinal))?.Slug
            ?? string.Empty;

        return new ProposalView(
            proposal.Id,
            seller.Handle,
            seller.MemberSince,
            proposal.ListingId,
            listing?.Title ?? string.Empty,
            listing is { Photos.Count: > 0 } ? listing.Photos[0].StorageKey : null,
            slug,
            listing?.Condition ?? string.Empty,
            listing?.City ?? string.Empty,
            proposal.ProposedPrice.Rupiah,
            (listing?.Price ?? proposal.ListingPriceAtProposal).Rupiah,
            proposal.ListingPriceAtProposal.Rupiah,
            proposal.IsUpsell(bounty.MaxBudget),
            proposal.AmountOverBudget(bounty.MaxBudget).Rupiah,
            proposal.Note,
            proposal.Status.ToString(),
            proposal.LapseReason,
            proposal.CreatedAt,
            proposal.OfferId);
    }

    private async Task<List<Bounty>> ReadWindowsAsync(IReadOnlyList<Category> wanted, int perCategory, CancellationToken ct)
    {
        var windows = await Task.WhenAll(wanted.Select(c => bounties.ListOpenAsync(c.Id, perCategory, ct))).ConfigureAwait(false);

        // The board index drops a Dicari when it closes or a deal starts, but one that merely ran past its deadline stays
        // until the sweep reaches it: the clock decides what is shown.
        return windows.SelectMany(w => w).Where(b => b.AcceptsProposals(clock)).ToList();
    }

    private readonly record struct Identity(string Handle, DateTimeOffset MemberSince);

    private async Task<Identity> IdentityAsync(string userId, DateTimeOffset fallbackSince, CancellationToken ct)
    {
        var profile = await publicProfiles.FindByUserAsync(userId, ct).ConfigureAwait(false);
        return profile is null ? new Identity(string.Empty, fallbackSince) : new Identity(profile.Handle, profile.MemberSince);
    }

    private static List<Bounty> Sort(List<Bounty> open, BountySort sort) => sort switch
    {
        BountySort.BudgetHigh => [.. open.OrderByDescending(b => b.MaxBudget.Rupiah).ThenByDescending(b => b.Id, StringComparer.Ordinal)],
        BountySort.EndingSoon => [.. open.OrderBy(b => b.ExpiresAt).ThenBy(b => b.Id, StringComparer.Ordinal)],
        BountySort.FewestProposals => [.. open.OrderBy(b => b.ProposalCount).ThenByDescending(b => b.Id, StringComparer.Ordinal)],
        _ => [.. open.OrderByDescending(b => b.Id, StringComparer.Ordinal)],
    };

    private async Task<IReadOnlyList<BountyCard>> ToCardsAsync(
        List<Bounty> items, IReadOnlyList<Category> all, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var handles = await publicProfiles.FindManyByUserAsync([.. items.Select(b => b.BuyerId).Distinct(StringComparer.Ordinal)], ct).ConfigureAwait(false);

        return [.. items.Select(b => new BountyCard(
            b.Id,
            b.Code,
            b.Title,
            b.CategoryId,
            all.FirstOrDefault(c => string.Equals(c.Id, b.CategoryId, StringComparison.Ordinal))?.Slug ?? string.Empty,
            b.Facets,
            b.MaxBudget.Rupiah,
            b.ShipToCity,
            handles.TryGetValue(b.BuyerId, out var profile) ? profile.Handle : string.Empty,
            b.ProposalCount,
            b.CreatedAt,
            b.ExpiresAt,
            b.EffectiveStatus(clock).ToString(),
            b.PhotoKeys.Count > 0 ? b.PhotoKeys[0] : null))];
    }
}
