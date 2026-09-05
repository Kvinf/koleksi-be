using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Listings;

/// <summary>
/// Creates a listing, and — when the caller asked to publish rather than save a draft —
/// makes it real: publishes it, opens its auction if it has one, and indexes it so it
/// actually appears in browse.
/// </summary>
/// <remarks>
/// <para>
/// The seller comes from <see cref="ICurrentUser"/>, never from the request body, for the
/// same reason <c>PlaceBidHandler</c> reads the bidder that way: a body that named its own
/// seller would let anyone list under anyone else's account.
/// </para>
/// <para>
/// <b>Why this is the line that makes browse work.</b>
/// <see cref="IListingSearchIndex.IndexAsync"/> has existed and been implemented since the
/// browse projection was built, but nothing in the product has ever called it — a
/// published listing has had nowhere to become visible from. This handler is that call.
/// </para>
/// </remarks>
public sealed class CreateListingHandler(
    IListingRepository listings,
    IAuctionRepository auctions,
    IListingSearchIndex searchIndex,
    ICategoryRepository categories,
    IMediaStorage media,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<CreateListingResult> HandleAsync(CreateListingCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sellerId = currentUser.RequireUserId();
        var sellerUsername = currentUser.Username
            ?? throw DomainException.For("listing.no_username", "Your account is missing a username.");

        var category = await categories.FindByIdAsync(command.CategoryId, ct).ConfigureAwait(false)
            ?? throw DomainException.For("listing.category_not_found", "Choose a category from the list.");

        var price = Money.TryFromRupiah(command.PriceRupiah)
            ?? throw DomainException.For("listing.invalid_price", "Price must be a positive number.");

        // The grant constrained the upload; this confirms something was actually put
        // there before the key is attached to a listing anyone can browse to — same
        // check the profile-avatar endpoint runs on its own upload.
        foreach (var photo in command.Photos)
        {
            if (!await media.VerifyUploadedAsync(photo.StorageKey, ct).ConfigureAwait(false))
            {
                throw DomainException.For(
                    "listing.photo_not_uploaded", "One of the photos was not actually uploaded.");
            }
        }

        var listing = Listing.CreateDraft(
            sellerId,
            command.CategoryId,
            command.Title,
            price,
            command.Format,
            command.Condition,
            command.City,
            clock);

        listing.SetDescription(command.Description, clock);

        if (command.Photos.Count > 0)
        {
            listing.SetPhotos(
                command.Photos.Select(photo => ListingPhoto.Create(photo.StorageKey, photo.Width, photo.Height)),
                clock);
        }

        if (command.Format == ListingFormat.AcceptOffers && command.AutoDeclineBelowPercent is { } percent)
        {
            listing.SetAutoDeclineThreshold(percent, clock);
        }

        await listings.AddAsync(listing, ct).ConfigureAwait(false);

        if (!command.Publish)
        {
            return new CreateListingResult(listing.Id, Published: false, CanonicalPath: null);
        }

        var expectedVersion = listing.Version;

        // Throws listing.photo_required when there are none — the caller's problem,
        // propagated unchanged, same as every other domain rule in this codebase.
        listing.Publish(clock);
        await listings.UpdateAsync(listing, expectedVersion, ct).ConfigureAwait(false);

        DateTimeOffset? auctionEndsAt = null;
        if (command.Format == ListingFormat.Auction)
        {
            var duration = TimeSpan.FromDays(command.AuctionDurationDays ?? 1);
            var reservePrice = command.ReservePriceRupiah is { } reserveRupiah
                ? Money.TryFromRupiah(reserveRupiah)
                    ?? throw DomainException.For("auction.invalid_reserve", "Reserve price must be a positive number.")
                : (Money?)null;
            var buyoutPrice = command.BuyoutPriceRupiah is { } buyoutRupiah
                ? Money.TryFromRupiah(buyoutRupiah)
                    ?? throw DomainException.For("auction.invalid_buyout", "Buyout price must be a positive number.")
                : (Money?)null;
            var auction = Auction.Create(listing.Id, sellerId, price, duration, clock, reservePrice, buyoutPrice);

            // Opened immediately rather than left Scheduled: this product has no
            // "list now, start bidding later" concept, and StartsAt is set to the
            // moment of creation, so the auction is already startable.
            auction.Open(clock);
            await auctions.AddAsync(auction, ct).ConfigureAwait(false);
            auctionEndsAt = auction.EndsAt;
        }

        await searchIndex.IndexAsync(
            new ListingIndexEntry(listing, sellerUsername, auctionEndsAt, BidCount: 0), ct).ConfigureAwait(false);

        return new CreateListingResult(listing.Id, Published: true, listing.UrlPath(category.Slug));
    }
}

/// <param name="Photos">Empty is fine for a draft; publishing requires at least one.</param>
/// <param name="AutoDeclineBelowPercent">Only applied when <see cref="Format"/> is <c>AcceptOffers</c>.</param>
/// <param name="AuctionDurationDays">Only read when <see cref="Format"/> is <c>Auction</c>; defaults to 1.</param>
/// <param name="ReservePriceRupiah">
/// Only read when <see cref="Format"/> is <c>Auction</c>. Shown openly next to the starting
/// bid — not a hidden reserve. <c>null</c> means any bid wins.
/// </param>
/// <param name="BuyoutPriceRupiah">
/// Only read when <see cref="Format"/> is <c>Auction</c>. A fixed price that ends the
/// auction immediately as a sale, at any point in bidding. <c>null</c> means no buyout.
/// </param>
/// <param name="Publish">
/// <c>false</c> saves a draft. <c>true</c> publishes immediately — the sell form's two
/// submit buttons are the only two states this product has; there is no separate
/// "edit a saved draft later and publish then" step yet.
/// </param>
public sealed record CreateListingCommand(
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
    bool Publish);

public sealed record PhotoInput(string StorageKey, int Width, int Height);

public sealed record CreateListingResult(string ListingId, bool Published, string? CanonicalPath);
