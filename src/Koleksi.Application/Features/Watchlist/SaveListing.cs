using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Watchlist;

/// <summary>Saves a listing to the authenticated caller's watchlist.</summary>
/// <remarks>
/// Naturally idempotent — saving what is already saved changes nothing, including the
/// listing's watcher count — so, like the cart, it is not wrapped in
/// <see cref="IIdempotencyStore"/>. The one-row-one-count guarantee lives in the repository's
/// conditional write, not here.
/// </remarks>
public sealed class SaveListingHandler(
    IWatchlistRepository watchlist,
    IListingRepository listings,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<SaveListingResult> HandleAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var userId = currentUser.RequireUserId();

        // The id becomes part of a key, so a malformed one never gets that far.
        if (!EntityId.IsValid(listingId))
        {
            return SaveListingResult.NotFound;
        }

        var listing = await listings.FindAsync(listingId, ct).ConfigureAwait(false);

        // A draft or suspended listing is invisible to everyone but its seller and staff, and
        // this endpoint must not become a way to find out which ids exist behind that wall:
        // "not there" and "not yours to see" answer the same.
        if (listing is null || listing.Status is ListingStatus.Draft or ListingStatus.Suspended)
        {
            return SaveListingResult.NotFound;
        }

        var saved = await watchlist.ListAsync(userId, ct).ConfigureAwait(false);
        var alreadySaved = saved.Any(entry => string.Equals(entry.ListingId, listing.Id, StringComparison.Ordinal));

        WatchlistRules.EnsureCanSave(listing, userId, alreadySaved, saved.Count);

        var change = await watchlist.AddAsync(userId, listing.Id, clock.UtcNow, ct).ConfigureAwait(false);

        // The listing stopped being active between the read above and the write: the repository
        // refused rather than save and count a listing nobody can buy.
        if (change == WatchChange.ListingUnavailable)
        {
            throw DomainException.For("watchlist.not_active", "Only a listing that is for sale can be saved.");
        }

        return new SaveListingResult(Found: true, Changed: change == WatchChange.Changed);
    }
}

/// <param name="Found"><c>false</c> when the listing does not exist or is not visible.</param>
/// <param name="Changed"><c>false</c> when it was already saved and nothing was written.</param>
public sealed record SaveListingResult(bool Found, bool Changed)
{
    public static SaveListingResult NotFound { get; } = new(false, false);
}

/// <summary>Takes a listing off the authenticated caller's watchlist.</summary>
/// <remarks>
/// No look at the listing at all, on purpose: a listing that has sold, been archived or been
/// suspended since it was saved must still be removable, and one that no longer exists leaves
/// a row nobody else can clear.
/// </remarks>
public sealed class UnsaveListingHandler(IWatchlistRepository watchlist, ICurrentUser currentUser)
{
    /// <returns><c>true</c> when a saved listing was removed, <c>false</c> when it was not saved.</returns>
    public async Task<bool> HandleAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var userId = currentUser.RequireUserId();

        if (!EntityId.IsValid(listingId))
        {
            return false;
        }

        var change = await watchlist.RemoveAsync(userId, listingId, ct).ConfigureAwait(false);
        return change == WatchChange.Changed;
    }
}
