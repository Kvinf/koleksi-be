using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Listings;

/// <summary>
/// Edits an existing listing: the "Save draft" / "Publish listing" pair the sell form has for a
/// draft, and "Save changes" for one that is or has been live.
/// </summary>
/// <remarks>
/// <para>
/// Which of the two applies is decided by the listing, not the caller: a listing that has never
/// been live (<see cref="Listing.IsUnpublished"/>) is a draft and takes the whole form; anything
/// else is edited with <see cref="Listing.EditLive"/>, which changes only what is safe once buyers
/// can see it and refuses a Sold or Suspended listing outright. One endpoint, so the sell form
/// has one place to PUT regardless of the listing's state.
/// </para>
/// <para>
/// <b>Price changes and pending offers.</b> An offer records the asking price it was made
/// against (<c>Offer.AskingPrice</c>) and an accepted offer is checked out at the offer's own
/// amount, so changing a listing's price never rewrites, strands or re-prices an existing offer;
/// it only changes what new buyers are asked to pay. Checkout likewise snapshots the price onto
/// the order line when the order is created, so a change cannot alter an order already placed.
/// The checkout hold store has no read operation, so a price change made while a buyer is
/// mid-checkout is not refused; the buyer pays the price their order was created with.
/// </para>
/// </remarks>
public sealed class UpdateListingHandler(
    IListingRepository listings,
    IAuctionRepository auctions,
    IListingSearchIndex searchIndex,
    ICategoryRepository categories,
    IMediaStorage media,
    IListingHoldStore holds,
    ICurrentUser currentUser,
    IClock clock,
    PublicIdentityResolver identities)
{
    public async Task<UpdateListingResult> HandleAsync(UpdateListingCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var listing = await listings.FindAsync(command.ListingId, ct).ConfigureAwait(false)
            ?? throw DomainException.For("listing.not_found", "Listing not found.");

        // Checked before anything else — same reasoning as every other listing endpoint's
        // ownership check: it must refuse identically on every repeat, so it cannot sit
        // inside whatever idempotency or mutation logic follows.
        listing.EnsureOwnedBy(currentUser.RequireUserId());

        var sellerUsername = (await identities.ResolveAsync(listing.SellerId, ct).ConfigureAwait(false)).Handle;

        return listing.IsUnpublished
            ? await EditDraftAsync(listing, sellerUsername, command, ct).ConfigureAwait(false)
            : await EditLiveAsync(listing, sellerUsername, command, ct).ConfigureAwait(false);
    }

    private async Task<UpdateListingResult> EditDraftAsync(
        Listing listing, string sellerUsername, UpdateListingCommand command, CancellationToken ct)
    {
        var category = await categories.FindByIdAsync(command.CategoryId, ct).ConfigureAwait(false)
            ?? throw DomainException.For("listing.category_not_found", "Choose a category from the list.");

        var price = Money.TryFromRupiah(command.PriceRupiah)
            ?? throw DomainException.For("listing.invalid_price", "Price must be a positive number.");

        await VerifyPhotosAsync(listing.SellerId, command.Photos, alreadyAttached: new HashSet<string>(), ct).ConfigureAwait(false);

        var expectedVersion = listing.Version;

        listing.EditDraft(command.CategoryId, command.Title, price, command.Format, command.Condition, command.City, clock);
        listing.SetDescription(command.Description, clock);
        if (command.Format != ListingFormat.Auction && command.Stock is { } draftStock)
        {
            listing.SetStock(draftStock, clock);
        }

        listing.SetPhotos(
            command.Photos.Select(photo => ListingPhoto.Create(photo.StorageKey, photo.Width, photo.Height)),
            clock);
        listing.SetAutoDeclineThreshold(
            command.Format == ListingFormat.AcceptOffers ? command.AutoDeclineBelowPercent : null,
            clock);

        AuctionLifecycle.ApplyTerms(
            listing, command.Format, command.AuctionDurationDays, command.ReservePriceRupiah, command.BuyoutPriceRupiah, clock);

        if (!command.Publish)
        {
            await listings.UpdateAsync(listing, expectedVersion, ct).ConfigureAwait(false);
            return new UpdateListingResult(listing.Id, Published: false, CanonicalPath: null);
        }

        // Throws the first unmet publish rule (title length, price floor, condition, city,
        // photo) — the caller's problem, propagated unchanged, same as CreateListingHandler's
        // publish step. Nothing has been saved yet, so a refusal leaves the stored draft as it was.
        listing.Publish(clock);

        // Built before the listing is saved as Active: terms that break an auction rule (a
        // reserve above the buyout, a duration out of range) must refuse the publish, not leave
        // a live listing with nothing to bid on.
        var auction = command.Format == ListingFormat.Auction ? AuctionLifecycle.OpenFor(listing, clock) : null;

        await listings.UpdateAsync(listing, expectedVersion, ct).ConfigureAwait(false);

        DateTimeOffset? auctionEndsAt = null;
        if (auction is not null)
        {
            await auctions.AddAsync(auction, ct).ConfigureAwait(false);
            auctionEndsAt = auction.EndsAt;
        }

        await searchIndex.IndexAsync(
            new ListingIndexEntry(listing, sellerUsername, auctionEndsAt, BidCount: 0), ct).ConfigureAwait(false);

        return new UpdateListingResult(listing.Id, Published: true, listing.UrlPath(category.Slug));
    }

    private async Task<UpdateListingResult> EditLiveAsync(
        Listing listing, string sellerUsername, UpdateListingCommand command, CancellationToken ct)
    {
        // Refused up front so a Sold or Suspended listing does not cost a storage round trip per
        // photo before the domain says no.
        if (listing.Status is not (ListingStatus.Active or ListingStatus.Archived))
        {
            throw DomainException.For("listing.not_editable", $"A {listing.Status} listing cannot be edited.");
        }

        // The sell form sends these back unchanged (they are locked in the UI); a request that
        // differs is a hand-built one, and is refused rather than quietly ignored so nobody
        // believes a category or format change took effect.
        if (!string.Equals(command.CategoryId, listing.CategoryId, StringComparison.Ordinal))
        {
            throw DomainException.For("listing.category_immutable", "A published listing's category cannot be changed.");
        }

        if (command.Format != listing.Format)
        {
            throw DomainException.For("listing.format_immutable", "A published listing's format cannot be changed.");
        }

        if (!string.Equals(command.Title?.Trim(), listing.Title, StringComparison.Ordinal))
        {
            throw DomainException.For("listing.title_immutable", "A published listing's title cannot be changed.");
        }

        var price = Money.TryFromRupiah(command.PriceRupiah)
            ?? throw DomainException.For("listing.invalid_price", "Price must be a positive number.");

        // Only keys the listing does not already carry: those were verified when first attached,
        // and each check is a storage request per size.
        await VerifyPhotosAsync(
            listing.SellerId,
            command.Photos, listing.Photos.Select(photo => photo.StorageKey).ToHashSet(StringComparer.Ordinal), ct)
            .ConfigureAwait(false);

        // The auction's bid count is the one fact the listing cannot see for itself. Read here
        // and handed to the domain; a bid landing between this read and the save below is the
        // same narrow window the archive endpoint accepts.
        Auction? auction = null;
        if (listing.Format == ListingFormat.Auction)
        {
            auction = await auctions.FindAsync(listing.Id, ct).ConfigureAwait(false);
        }

        var expectedVersion = listing.Version;

        // Units on offer may be changed while live (a seller restocks, or sells one elsewhere), but never
        // below what is already sold or being paid for: that would orphan an order.
        if (command.Stock is { } newStock && listing.Format != ListingFormat.Auction && newStock != listing.Stock)
        {
            var usage = await holds.GetAvailabilityAsync(listing.Id, listing.Stock, ct).ConfigureAwait(false);
            if (newStock < usage.MinimumStock)
            {
                throw DomainException.For(
                    "listing.stock_below_committed",
                    $"At least {usage.MinimumStock} units are already sold or being paid for.");
            }

            listing.SetStock(newStock, clock);
        }

        listing.EditLive(
            price,
            command.Description,
            command.Condition,
            command.City,
            command.Photos.Select(photo => ListingPhoto.Create(photo.StorageKey, photo.Width, photo.Height)),
            auctionHasBids: auction is { BidCount: > 0 },
            clock);

        await listings.UpdateAsync(listing, expectedVersion, ct).ConfigureAwait(false);

        // Only an Active listing is in browse. An Archived one was removed from the index when it
        // was delisted and is put back by the publish endpoint, which indexes the then-current copy.
        if (listing.Status != ListingStatus.Active)
        {
            return new UpdateListingResult(listing.Id, Published: false, CanonicalPath: null);
        }

        await searchIndex.IndexAsync(
            new ListingIndexEntry(listing, sellerUsername, auction?.EndsAt, auction?.BidCount ?? 0), ct)
            .ConfigureAwait(false);

        var category = await categories.FindByIdAsync(listing.CategoryId, ct).ConfigureAwait(false);
        return new UpdateListingResult(
            listing.Id, Published: true, category is null ? null : listing.UrlPath(category.Slug));
    }

    // The grant constrained the upload; this confirms something was actually put there before the
    // key is attached to a listing anyone can browse to.
    private async Task VerifyPhotosAsync(
        string ownerId, IReadOnlyList<PhotoInput> photos, HashSet<string> alreadyAttached, CancellationToken ct)
    {
        foreach (var photo in photos)
        {
            if (alreadyAttached.Contains(photo.StorageKey))
            {
                continue;
            }

            if (!await media.VerifyOwnedUploadAsync(MediaPurpose.ListingPhoto, ownerId, photo.StorageKey, ct).ConfigureAwait(false))
            {
                throw DomainException.For(
                    "listing.photo_not_uploaded", "One of the photos was not actually uploaded.");
            }
        }
    }
}

/// <param name="Photos">The full replacement set. Empty is fine for a draft; a live listing keeps at least one.</param>
/// <param name="AutoDeclineBelowPercent">Draft only; only applied when <see cref="Format"/> is <c>AcceptOffers</c>, cleared otherwise.</param>
/// <param name="AuctionDurationDays">Draft only; only kept when <see cref="Format"/> is <c>Auction</c>; stored on the draft and used at publish.</param>
/// <param name="ReservePriceRupiah">Draft only; only kept for an auction. <c>null</c> means any bid wins.</param>
/// <param name="BuyoutPriceRupiah">Draft only; only kept for an auction. <c>null</c> means no buyout.</param>
/// <param name="Publish">Draft only: <c>false</c> keeps it a draft; <c>true</c> publishes in the same call. Ignored for a live listing.</param>
public sealed record UpdateListingCommand(
    string ListingId,
    string CategoryId,
    string Title,
    long PriceRupiah,
    ListingFormat Format,
    string Condition,
    string City,
    string Description,
    IReadOnlyList<PhotoInput> Photos,
    decimal? AutoDeclineBelowPercent,
    int? AuctionDurationDays,
    long? ReservePriceRupiah,
    long? BuyoutPriceRupiah,
    bool Publish,
    int? Stock = null);

public sealed record UpdateListingResult(string ListingId, bool Published, string? CanonicalPath);
