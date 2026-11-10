using Koleksi.Application.Abstractions;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Application.Features.Bounties;

/// <summary>Where a Dicari lives once posted, for the redirect after the form.</summary>
public sealed record BountyCreated(string BountyId, string Code);

/// <param name="Facets">Category filter values (<c>brand=pokemon</c>), checked against the category's schema.</param>
/// <param name="PhotoKeys">Base keys of photo sets uploaded with <see cref="MediaPurpose.BountyPhoto"/>.</param>
public sealed record PostBountyCommand(
    string Title,
    string CategoryId,
    IReadOnlyDictionary<string, string> Facets,
    string? Description,
    IReadOnlyList<string> Conditions,
    long MaxBudgetRupiah,
    IReadOnlyList<string> PhotoKeys,
    int DurationDays);

/// <summary>Posts a Dicari for the signed-in buyer. Free, with a cap on how many may be open at once.</summary>
public sealed class PostBountyHandler(
    IBountyRepository bounties,
    ICategoryRepository categories,
    IProfileRepository profiles,
    IMediaStorage media,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<BountyCreated> HandleAsync(PostBountyCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var buyerId = currentUser.RequireUserId();

        var category = await categories.FindByIdAsync(command.CategoryId, ct).ConfigureAwait(false)
            ?? throw DomainException.For("bounty.category_not_found", "Choose a category from the list.");
        category.EnsureAttributesAreValid(command.Facets);

        var maxBudget = Money.TryFromRupiah(command.MaxBudgetRupiah)
            ?? throw DomainException.For("bounty.invalid_budget", "The budget must be a positive number.");

        // The city a seller sees comes from the buyer's own default address, never from the request, so a Dicari cannot
        // claim a place the buyer does not ship to.
        var profile = await profiles.FindAsync(buyerId, ct).ConfigureAwait(false);
        var addresses = profile?.Addresses;
        if (addresses is null || addresses.Count == 0)
        {
            throw DomainException.For("bounty.address_required", "Add a delivery address before posting a Dicari.");
        }

        var city = addresses.FirstOrDefault(a => a.IsDefault) is { City: { Length: > 0 } defaultCity }
            ? defaultCity
            : addresses[0].City;

        if (await CountOpenAsync(bounties, buyerId, clock, ct).ConfigureAwait(false) >= Bounty.MaxOpenPerBuyer)
        {
            throw DomainException.For(
                "bounty.limit_reached", $"You can have at most {Bounty.MaxOpenPerBuyer} open Dicari at a time.");
        }

        foreach (var key in command.PhotoKeys)
        {
            if (!await media.VerifyOwnedUploadAsync(MediaPurpose.BountyPhoto, buyerId, key, ct).ConfigureAwait(false))
            {
                throw DomainException.For("bounty.photo_not_uploaded", "One of the photos was not actually uploaded.");
            }
        }

        var bounty = Bounty.Create(
            buyerId, null, command.Title, category.Id, command.Facets, command.Description, command.Conditions,
            maxBudget, city, command.PhotoKeys, command.DurationDays, clock);

        await bounties.AddAsync(bounty, ct).ConfigureAwait(false);
        return new BountyCreated(bounty.Id, bounty.Code);
    }

    /// <summary>
    /// Open Dicari the buyer holds, counted from their own list. Two posts racing past this can briefly exceed the cap
    /// by one; the cap is a spam limit, not money, so that window is accepted.
    /// </summary>
    internal static async Task<int> CountOpenAsync(IBountyRepository bounties, string buyerId, IClock clock, CancellationToken ct)
    {
        var page = await bounties.ListForBuyerAsync(buyerId, null, 50, ct).ConfigureAwait(false);
        return page.Items.Count(b => b.EffectiveStatus(clock) == BountyStatus.Open);
    }
}

public sealed record EditBountyCommand(
    string BountyId,
    string Title,
    string? Description,
    long MaxBudgetRupiah,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> PhotoKeys,
    string? CategoryId,
    IReadOnlyDictionary<string, string>? Facets);

/// <summary>Edits the buyer's own open Dicari. The category is locked once someone has proposed.</summary>
public sealed class EditBountyHandler(
    IBountyRepository bounties,
    ICategoryRepository categories,
    IMediaStorage media,
    BountyDeals deals,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<bool> HandleAsync(EditBountyCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var buyerId = currentUser.RequireUserId();
        var bounty = await OwnedAsync(bounties, command.BountyId, buyerId, ct).ConfigureAwait(false);
        if (bounty is null)
        {
            return false;
        }

        bounty = await deals.ApplyExpiryAsync(bounty, ct).ConfigureAwait(false);

        var maxBudget = Money.TryFromRupiah(command.MaxBudgetRupiah)
            ?? throw DomainException.For("bounty.invalid_budget", "The budget must be a positive number.");

        string? newCategory = null;
        if (command.CategoryId is { } requested && !string.Equals(requested, bounty.CategoryId, StringComparison.Ordinal))
        {
            var category = await categories.FindByIdAsync(requested, ct).ConfigureAwait(false)
                ?? throw DomainException.For("bounty.category_not_found", "Choose a category from the list.");
            category.EnsureAttributesAreValid(command.Facets ?? new Dictionary<string, string>());
            newCategory = category.Id;
        }
        else if (command.Facets is { } facets)
        {
            var category = await categories.FindByIdAsync(bounty.CategoryId, ct).ConfigureAwait(false);
            category?.EnsureAttributesAreValid(facets);
        }

        foreach (var key in command.PhotoKeys.Except(bounty.PhotoKeys, StringComparer.Ordinal))
        {
            if (!await media.VerifyOwnedUploadAsync(MediaPurpose.BountyPhoto, buyerId, key, ct).ConfigureAwait(false))
            {
                throw DomainException.For("bounty.photo_not_uploaded", "One of the photos was not actually uploaded.");
            }
        }

        var expectedVersion = bounty.Version;
        bounty.Edit(
            buyerId, command.Title, command.Description, maxBudget, command.Conditions, command.PhotoKeys,
            newCategory, command.Facets ?? (newCategory is null ? null : new Dictionary<string, string>()), clock);

        await bounties.UpdateAsync(bounty, expectedVersion, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>A stranger gets the answer a missing Dicari gets, so ids cannot be probed.</summary>
    internal static async Task<Bounty?> OwnedAsync(IBountyRepository bounties, string bountyId, string buyerId, CancellationToken ct)
    {
        var bounty = await bounties.FindAsync(bountyId, ct).ConfigureAwait(false);
        return bounty is not null && string.Equals(bounty.BuyerId, buyerId, StringComparison.Ordinal) ? bounty : null;
    }
}

/// <summary>Closes the buyer's own Dicari and lapses the proposals still waiting on it.</summary>
public sealed class CloseBountyHandler(
    IBountyRepository bounties,
    BountyDeals deals,
    BountyNotifier notifier,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<bool> HandleAsync(string bountyId, CancellationToken ct)
    {
        var buyerId = currentUser.RequireUserId();
        var bounty = await EditBountyHandler.OwnedAsync(bounties, bountyId, buyerId, ct).ConfigureAwait(false);
        if (bounty is null)
        {
            return false;
        }

        var expectedVersion = bounty.Version;
        bounty.Close(buyerId, clock);
        await bounties.UpdateAsync(bounty, expectedVersion, ct).ConfigureAwait(false);

        await deals.LapsePendingAsync(bounty, BountyDeals.Reasons.BountyEnded, ct).ConfigureAwait(false);
        await notifier.NotifyAsync(buyerId, bounty.Id, ct).ConfigureAwait(false);
        return true;
    }
}

public sealed record MakeProposalCommand(string BountyId, string ListingId, long PriceRupiah, string? Note);

/// <summary>A seller answers a Dicari with one of their own listings at a price.</summary>
public sealed class MakeProposalHandler(
    IBountyRepository bounties,
    IListingRepository listings,
    BountyDeals deals,
    BountyNotifier notifier,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<string?> HandleAsync(MakeProposalCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sellerId = currentUser.RequireUserId();

        var bounty = await bounties.FindAsync(command.BountyId, ct).ConfigureAwait(false);
        if (bounty is null || bounty.Status == BountyStatus.Removed)
        {
            return null;
        }

        bounty = await deals.ApplyExpiryAsync(bounty, ct).ConfigureAwait(false);

        var listing = await listings.FindAsync(command.ListingId, ct).ConfigureAwait(false)
            ?? throw DomainException.For("bounty.listing_not_found", "Choose one of your own active listings.");

        var price = Money.TryFromRupiah(command.PriceRupiah)
            ?? throw DomainException.For("bounty.invalid_price", "The price must be a positive number.");

        var proposal = BountyProposal.Create(bounty, listing, sellerId, price, command.Note, null, clock);

        try
        {
            await bounties.AddProposalAsync(proposal, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw DomainException.For("bounty.already_proposed", "You already have a proposal on this Dicari.");
        }

        await notifier.NotifyAsync(bounty.BuyerId, bounty.Id, ct).ConfigureAwait(false);
        return proposal.Id;
    }
}

/// <summary>The seller takes their proposal back.</summary>
public sealed class WithdrawProposalHandler(
    IBountyRepository bounties, BountyNotifier notifier, ICurrentUser currentUser)
{
    public async Task<bool> HandleAsync(string bountyId, string proposalId, CancellationToken ct)
    {
        var sellerId = currentUser.RequireUserId();
        var proposal = await bounties.FindProposalAsync(bountyId, proposalId, ct).ConfigureAwait(false);
        if (proposal is null || !string.Equals(proposal.SellerId, sellerId, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedVersion = proposal.Version;
        proposal.Withdraw(sellerId);
        await bounties.UpdateProposalAsync(proposal, expectedVersion, ct).ConfigureAwait(false);

        if (await bounties.FindAsync(bountyId, ct).ConfigureAwait(false) is { } bounty)
        {
            await notifier.NotifyAsync(bounty.BuyerId, bounty.Id, ct).ConfigureAwait(false);
        }

        return true;
    }
}

/// <summary>The buyer turns a proposal down.</summary>
public sealed class DeclineProposalHandler(
    IBountyRepository bounties, BountyNotifier notifier, ICurrentUser currentUser)
{
    public async Task<bool> HandleAsync(string bountyId, string proposalId, CancellationToken ct)
    {
        var buyerId = currentUser.RequireUserId();
        var bounty = await EditBountyHandler.OwnedAsync(bounties, bountyId, buyerId, ct).ConfigureAwait(false);
        var proposal = bounty is null ? null : await bounties.FindProposalAsync(bountyId, proposalId, ct).ConfigureAwait(false);
        if (bounty is null || proposal is null)
        {
            return false;
        }

        var expectedVersion = proposal.Version;
        proposal.Decline(buyerId, bounty);
        await bounties.UpdateProposalAsync(proposal, expectedVersion, ct).ConfigureAwait(false);
        await notifier.NotifyAsync(proposal.SellerId, bounty.Id, ct).ConfigureAwait(false);
        return true;
    }
}

/// <param name="OfferId">The accepted offer the buyer now checks out. Its id is not the Dicari's or the proposal's.</param>
public sealed record ProposalAccepted(
    string BountyId, string ProposalId, string OfferId, long PriceRupiah, bool IsUpsell, long AmountOverBudgetRupiah,
    DateTimeOffset CheckoutBy);

/// <summary>
/// The buyer accepts a proposal: the Dicari is claimed for the deal, an already-accepted offer is made on the
/// seller's listing at the proposed price, and the buyer pays through the normal checkout.
/// </summary>
/// <remarks>
/// An upsell (a price over the budget) is accepted exactly the same way; the screen asks the buyer to confirm it
/// first. Order of writes: the Dicari is claimed first (a conditional write, so two accepts cannot both win), then
/// the offer, then the proposal. If the offer cannot be written the claim is handed back.
/// </remarks>
public sealed class AcceptProposalHandler(
    IBountyRepository bounties,
    IListingRepository listings,
    IOfferRepository offers,
    BountyDeals deals,
    BountyNotifier notifier,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<ProposalAccepted?> HandleAsync(string bountyId, string proposalId, CancellationToken ct)
    {
        var buyerId = currentUser.RequireUserId();

        var bounty = await EditBountyHandler.OwnedAsync(bounties, bountyId, buyerId, ct).ConfigureAwait(false);
        var proposal = bounty is null ? null : await bounties.FindProposalAsync(bountyId, proposalId, ct).ConfigureAwait(false);
        if (bounty is null || proposal is null)
        {
            return null;
        }

        bounty = await deals.ApplyExpiryAsync(bounty, ct).ConfigureAwait(false);

        var listing = await listings.FindAsync(proposal.ListingId, ct).ConfigureAwait(false);
        if (listing is null)
        {
            await deals.LapseBecauseListingGoneAsync(proposal, ct).ConfigureAwait(false);
            throw DomainException.For(BountyDeals.Reasons.ListingUnavailable, "This proposal's listing can no longer be bought.");
        }

        if (await offers.FindActiveAsync(listing.Id, buyerId, ct).ConfigureAwait(false) is not null)
        {
            throw DomainException.For("offer.already_active", "You already have an open offer on this listing.");
        }

        var bountyVersion = bounty.Version;
        var proposalVersion = proposal.Version;
        var offer = proposal.Accept(bounty, buyerId, listing, null, clock);

        try
        {
            await bounties.UpdateAsync(bounty, bountyVersion, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw DomainException.For("bounty.deal_in_progress", "Another proposal was just accepted on this Dicari.");
        }

        var offerAdded = false;
        try
        {
            await offers.AddAsync(offer, ct).ConfigureAwait(false);
            offerAdded = true;
            await bounties.UpdateProposalAsync(proposal, proposalVersion, ct).ConfigureAwait(false);
        }
        catch
        {
            // Take the offer back first (it must never stay payable without a deal behind it), then hand the claim
            // back so the Dicari is not stuck waiting. Each step is best effort so the original error is the one seen.
            if (offerAdded)
            {
                await RetireOfferAsync(offer, ct).ConfigureAwait(false);
            }

            await deals.ReleaseDealAsync(bounty, null, ct).ConfigureAwait(false);
            throw;
        }

        await notifier.NotifyAsync(proposal.SellerId, bounty.Id, ct).ConfigureAwait(false);

        return new ProposalAccepted(
            bounty.Id, proposal.Id, offer.Id, proposal.ProposedPrice.Rupiah, proposal.IsUpsell(bounty.MaxBudget),
            proposal.AmountOverBudget(bounty.MaxBudget).Rupiah, offer.ExpiresAt);
    }

    private async Task RetireOfferAsync(Offer offer, CancellationToken ct)
    {
        try
        {
            var version = offer.Version;
            offer.CancelUnusedBountyOffer();
            await offers.UpdateAsync(offer, version, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is DomainException or ConcurrencyConflictException)
        {
            // The sweep expires the offer at its own deadline; there is nothing more to do here.
        }
    }
}
