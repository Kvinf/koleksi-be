using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Catalog;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IListingRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads hand back a <b>copy</b>, never the stored instance. That is not tidiness: the
/// production repository rehydrates a fresh aggregate from DynamoDB on every read, so a
/// fake that shares one object would let a handler's mutations appear in storage without
/// a write ever succeeding — and every optimistic-concurrency assertion built on top of
/// it would be meaningless.
/// </para>
/// <para>
/// Writes therefore enforce the same version condition the real conditional write does.
/// </para>
/// </remarks>
public sealed class FakeListingRepository : FakeAdapter, IListingRepository
{
    private readonly Dictionary<string, Listing> _listings = new(StringComparer.Ordinal);

    /// <summary>Listing ids whose view counter was bumped, in call order.</summary>
    public List<string> ViewIncrements { get; } = [];

    /// <summary>
    /// Stands in for the browse projection's denormalised seller handle. Empty by default —
    /// a test that needs <see cref="FindSellerUsernameAsync"/> to answer something populates
    /// this directly, the same way production only has an entry once a listing is indexed.
    /// </summary>
    public Dictionary<string, string> SellerUsernames { get; } = new(StringComparer.Ordinal);

    /// <summary>Seeds storage directly, bypassing the write path.</summary>
    public void Seed(Listing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        _listings[listing.Id] = listing;
    }

    public Task<Listing?> FindAsync(string listingId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(_listings.TryGetValue(listingId, out var listing) ? Copy(listing) : null);
    }

    public Task AddAsync(Listing listing, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(listing);

        if (!_listings.TryAdd(listing.Id, Copy(listing)))
        {
            throw new ConcurrencyConflictException($"Listing {listing.Id} already exists.");
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Listing listing, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(listing);

        if (!_listings.TryGetValue(listing.Id, out var stored) || stored.Version != expectedVersion)
        {
            throw new ConcurrencyConflictException($"Listing {listing.Id} changed concurrently.");
        }

        _listings[listing.Id] = Copy(listing);
        return Task.CompletedTask;
    }

    public Task<Page<Listing>> ListBySellerAsync(
        string sellerId, ListingStatus? status, string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();

        // The cursor is the id of the last row of the previous page, which is all an id-ordered
        // list needs to resume from; production's is an opaque DynamoDB key, but a caller only
        // ever hands it back unread.
        var ordered = _listings.Values
            .Where(listing => string.Equals(listing.SellerId, sellerId, StringComparison.Ordinal))
            .Where(listing => status is null || listing.Status == status)
            .OrderBy(listing => listing.Id, StringComparer.Ordinal)
            .Where(listing => cursor is null || string.CompareOrdinal(listing.Id, cursor) > 0)
            .ToList();

        var matches = ordered.Take(limit).Select(Copy).ToList();
        var next = ordered.Count > limit ? matches[^1].Id : null;

        return Task.FromResult(new Page<Listing>(matches, next, matches.Count));
    }

    public Task IncrementViewCountAsync(string listingId, CancellationToken ct)
    {
        ThrowIfFailing();
        ViewIncrements.Add(listingId);

        if (_listings.TryGetValue(listingId, out var listing))
        {
            listing.ApplyViewCount(listing.ViewCount + 1);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stands in for the watchlist's atomic ADD on the listing row, which production applies
    /// in the same transaction as the saved-item row. Returns <c>false</c> when the listing
    /// does not exist or, for a decrement, when the count is already zero.
    /// </summary>
    public bool TryAdjustWatcherCount(string listingId, long delta)
    {
        if (!_listings.TryGetValue(listingId, out var listing) || listing.WatcherCount + delta < 0)
        {
            return false;
        }

        listing.ApplyWatcherCount(listing.WatcherCount + delta);
        return true;
    }

    public Task<string?> FindSellerUsernameAsync(string listingId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(SellerUsernames.TryGetValue(listingId, out var username) ? username : null);
    }

    public Task<int> CountBySellerAsync(string sellerId, ListingStatus status, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(_listings.Values.Count(l => l.SellerId == sellerId && l.Status == status));
    }

    public Task<IReadOnlyDictionary<string, string>> FindSellerIdsAsync(
        IReadOnlyCollection<string> listingIds, CancellationToken ct)
    {
        ThrowIfFailing();
        IReadOnlyDictionary<string, string> found = listingIds
            .Distinct(StringComparer.Ordinal)
            .Where(_listings.ContainsKey)
            .ToDictionary(id => id, id => _listings[id].SellerId, StringComparer.Ordinal);
        return Task.FromResult(found);
    }

    private static Listing Copy(Listing listing) => Listing.Rehydrate(
        listing.Id,
        listing.SellerId,
        listing.CategoryId,
        listing.Title,
        listing.Description,
        listing.Price,
        listing.Format,
        listing.Status,
        listing.Condition,
        listing.City,
        listing.Photos,
        listing.Attributes,
        listing.ViewCount,
        listing.WatcherCount,
        listing.AutoDeclineBelowPercent,
        listing.CreatedAt,
        listing.UpdatedAt,
        listing.PublishedAt,
        listing.Version,
        listing.AuctionDurationDays,
        listing.AuctionReservePrice,
        listing.AuctionBuyoutPrice,
        listing.Stock);
}
