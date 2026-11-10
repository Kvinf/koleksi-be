using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Domain.Bounties;

/// <summary>Where a seller's proposal on a Dicari stands.</summary>
public enum ProposalStatus
{
    Pending = 0,

    /// <summary>The buyer accepted it; an offer was made on the listing at the proposed price.</summary>
    Accepted = 1,

    /// <summary>The buyer refused it, or accepted a different one.</summary>
    Declined = 2,

    /// <summary>The seller took it back.</summary>
    Withdrawn = 3,

    /// <summary>It can no longer be taken up: the listing sold out or left sale, or the Dicari ended.</summary>
    Lapsed = 4,
}

/// <summary>
/// A seller's answer to a Dicari: one of their own listings, at a price for that buyer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Upsell.</b> A proposal priced above the buyer's <see cref="Bounty.MaxBudget"/> is allowed (it still cannot
/// exceed the listing's own price). It is an upsell: flagged to the buyer, but once accepted it is exactly like
/// any other accepted proposal. Whether it is an upsell is derived from the budget as it stands (see
/// <see cref="IsUpsell"/>), never stored, so editing the budget re-labels it truthfully.
/// </para>
/// <para>
/// <b>Ids.</b> The proposal, its Dicari and the offer made on acceptance each have their own id. The checkout URL
/// carries the offer's, so neither of the other two leaks into it.
/// </para>
/// </remarks>
public sealed class BountyProposal
{
    public const int MaxNoteLength = 500;

    private BountyProposal(
        string id,
        string bountyId,
        string sellerId,
        string? sellerUsername,
        string listingId,
        Money proposedPrice,
        Money listingPriceAtProposal,
        string? note,
        DateTimeOffset createdAt)
    {
        Id = id;
        BountyId = bountyId;
        SellerId = sellerId;
        SellerUsername = sellerUsername;
        ListingId = listingId;
        ProposedPrice = proposedPrice;
        ListingPriceAtProposal = listingPriceAtProposal;
        Note = note;
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public string BountyId { get; }
    public string SellerId { get; }

    /// <summary>The seller's public handle when they proposed.</summary>
    public string? SellerUsername { get; }

    public string ListingId { get; }
    public Money ProposedPrice { get; }

    /// <summary>The listing's price when the proposal was made, so the buyer sees what the seller is giving up.</summary>
    public Money ListingPriceAtProposal { get; }

    public string? Note { get; }
    public DateTimeOffset CreatedAt { get; }
    public ProposalStatus Status { get; private set; } = ProposalStatus.Pending;

    /// <summary>The offer created when the buyer accepted. A different id from this proposal's and the Dicari's.</summary>
    public string? OfferId { get; private set; }

    /// <summary>Why a lapsed proposal lapsed (a stable code the UI turns into words), when it did.</summary>
    public string? LapseReason { get; private set; }

    public long Version { get; private set; }

    /// <summary>Whether this proposal is priced above <paramref name="maxBudget"/>. Derived, never stored.</summary>
    public bool IsUpsell(Money maxBudget) => ProposedPrice > maxBudget;

    /// <summary>How far above <paramref name="maxBudget"/> the proposal is, or zero when it is within budget.</summary>
    public Money AmountOverBudget(Money maxBudget) => IsUpsell(maxBudget) ? ProposedPrice - maxBudget : Money.Zero;

    /// <summary>Makes a proposal on <paramref name="bounty"/>, built on <paramref name="listing"/>.</summary>
    /// <param name="sellerId">The proposing account (the caller), who must own the listing.</param>
    public static BountyProposal Create(
        Bounty bounty,
        Listing listing,
        string sellerId,
        Money proposedPrice,
        string? note,
        string? sellerUsername,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(bounty);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.Equals(bounty.BuyerId, sellerId, StringComparison.Ordinal))
        {
            throw DomainException.For("bounty.self_proposal", "You cannot make a proposal on your own Dicari.");
        }

        if (!bounty.AcceptsProposals(clock))
        {
            throw DomainException.For(
                bounty.DealInFlight ? "bounty.deal_in_progress" : "bounty.not_open",
                "This Dicari is not taking proposals.");
        }

        EnsureListingFits(bounty, listing, sellerId);

        if (proposedPrice.IsZero)
        {
            throw DomainException.For("bounty.invalid_price", "The proposed price must be more than zero.");
        }

        if (proposedPrice > listing.Price)
        {
            throw DomainException.For(
                "bounty.price_above_listing", "A proposal cannot be priced above the listing's own price.");
        }

        var cleanNote = CleanNote(note);
        var now = clock.UtcNow;
        return new BountyProposal(
            EntityId.New(now), bounty.Id, sellerId, sellerUsername, listing.Id, proposedPrice, listing.Price, cleanNote, now);
    }

    public static BountyProposal Rehydrate(
        string id,
        string bountyId,
        string sellerId,
        string? sellerUsername,
        string listingId,
        Money proposedPrice,
        Money listingPriceAtProposal,
        string? note,
        DateTimeOffset createdAt,
        ProposalStatus status,
        string? offerId,
        string? lapseReason,
        long version) =>
        new(id, bountyId, sellerId, sellerUsername, listingId, proposedPrice, listingPriceAtProposal, note, createdAt)
        {
            Status = status,
            OfferId = offerId,
            LapseReason = lapseReason,
            Version = version,
        };

    /// <summary>
    /// Whether <paramref name="listing"/> can back a proposal on <paramref name="bounty"/> right now; the reason
    /// as a stable code otherwise. Used to refuse a new proposal and to lapse an existing one.
    /// </summary>
    public static string? WhyListingDoesNotFit(Bounty bounty, Listing? listing, string sellerId)
    {
        ArgumentNullException.ThrowIfNull(bounty);

        if (listing is null)
        {
            return "bounty.listing_unavailable";
        }

        if (!string.Equals(listing.SellerId, sellerId, StringComparison.Ordinal))
        {
            return "bounty.listing_not_yours";
        }

        if (listing.Status != ListingStatus.Active)
        {
            return "bounty.listing_unavailable";
        }

        if (listing.Format == ListingFormat.Auction)
        {
            return "bounty.listing_is_auction";
        }

        if (!string.Equals(listing.CategoryId, bounty.CategoryId, StringComparison.Ordinal))
        {
            return "bounty.category_mismatch";
        }

        return listing.Stock < 1 ? "bounty.listing_out_of_stock" : null;
    }

    /// <summary>
    /// The buyer accepts this proposal. Returns the offer to store: the listing at <see cref="ProposedPrice"/>,
    /// already accepted, with the ordinary 48 h checkout window. The Dicari starts waiting on the deal and its
    /// <see cref="Bounty.MaxBudget"/> is left as it was, upsell or not.
    /// </summary>
    /// <remarks>
    /// The caller declines the other pending proposals and stores everything; this method changes only this
    /// proposal and the Dicari, and hands back the offer.
    /// </remarks>
    public Offer Accept(Bounty bounty, string actingBuyerId, Listing listing, string? buyerUsername, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(bounty);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(clock);

        if (!string.Equals(bounty.Id, BountyId, StringComparison.Ordinal))
        {
            throw DomainException.For("bounty.proposal_mismatch", "This proposal belongs to a different Dicari.");
        }

        bounty.EnsureOwnedBy(actingBuyerId);

        if (Status != ProposalStatus.Pending)
        {
            throw DomainException.For("bounty.proposal_not_pending", $"This proposal is already {Status}.");
        }

        // Re-checked at the moment of acceptance: the listing may have sold, been delisted or been repriced
        // since the proposal was made, and a deal on it would be dead on arrival.
        if (WhyListingDoesNotFit(bounty, listing, SellerId) is { } why)
        {
            throw DomainException.For(why, "This proposal's listing can no longer be bought.");
        }

        if (!string.Equals(listing.Id, ListingId, StringComparison.Ordinal))
        {
            throw DomainException.For("bounty.proposal_mismatch", "This proposal belongs to a different listing.");
        }

        if (ProposedPrice > listing.Price)
        {
            throw DomainException.For(
                "bounty.proposal_price_stale", "The listing's price dropped below this proposal. Ask the seller to propose again.");
        }

        var offer = Offer.CreateAcceptedFromBounty(
            ListingId, bounty.BuyerId, SellerId, ProposedPrice, listing.Price, bounty.Id, Id, clock, buyerUsername, SellerUsername);

        // BeginDeal also refuses a Dicari that is no longer open or already has a deal in flight.
        bounty.BeginDeal(Id, clock);

        Status = ProposalStatus.Accepted;
        OfferId = offer.Id;
        Version++;
        return offer;
    }

    /// <summary>The buyer refuses this proposal, or accepted another one.</summary>
    public void Decline(string actingBuyerId, Bounty bounty)
    {
        ArgumentNullException.ThrowIfNull(bounty);
        bounty.EnsureOwnedBy(actingBuyerId);
        DeclineSystem();
    }

    /// <summary>Declines a pending proposal because a different one was accepted. No actor check: the accepting handler already made it.</summary>
    public void DeclineSystem()
    {
        EnsurePending();
        Status = ProposalStatus.Declined;
        Version++;
    }

    /// <summary>The seller takes the proposal back, freeing their one slot on this Dicari.</summary>
    public void Withdraw(string actingSellerId)
    {
        if (!string.Equals(SellerId, actingSellerId, StringComparison.Ordinal))
        {
            throw DomainException.For("bounty.forbidden", "Only the seller who made this proposal can withdraw it.");
        }

        EnsurePending();
        Status = ProposalStatus.Withdrawn;
        Version++;
    }

    /// <summary>
    /// The proposal can no longer be taken up. Applies to a pending one (its listing sold or left sale, or the
    /// Dicari ended) and to an accepted one whose offer lapsed unpaid. Idempotent on one already lapsed.
    /// </summary>
    public void Lapse(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is ProposalStatus.Lapsed or ProposalStatus.Declined or ProposalStatus.Withdrawn)
        {
            return;
        }

        Status = ProposalStatus.Lapsed;
        LapseReason = reason;
        Version++;
    }

    private void EnsurePending()
    {
        if (Status != ProposalStatus.Pending)
        {
            throw DomainException.For("bounty.proposal_not_pending", $"This proposal is already {Status}.");
        }
    }

    private static void EnsureListingFits(Bounty bounty, Listing listing, string sellerId)
    {
        if (WhyListingDoesNotFit(bounty, listing, sellerId) is { } why)
        {
            throw DomainException.For(why, ReasonText(why));
        }
    }

    private static string ReasonText(string code) => code switch
    {
        "bounty.listing_not_yours" => "You can only propose one of your own listings.",
        "bounty.listing_unavailable" => "That listing is not for sale right now.",
        "bounty.listing_is_auction" => "An auction cannot be offered on a Dicari. Use a fixed-price or nego listing.",
        "bounty.category_mismatch" => "That listing is in a different category from this Dicari.",
        "bounty.listing_out_of_stock" => "That listing has no stock left.",
        _ => "That listing cannot be offered here.",
    };

    private static string? CleanNote(string? note)
    {
        var trimmed = note?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxNoteLength)
        {
            throw DomainException.For("bounty.note_too_long", $"The note is at most {MaxNoteLength} characters.");
        }

        Bounty.RejectContactInfo(trimmed);
        return trimmed;
    }
}
