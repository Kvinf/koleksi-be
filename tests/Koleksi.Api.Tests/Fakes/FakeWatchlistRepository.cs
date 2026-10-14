using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IWatchlistRepository"/> that keeps the production contract: a row
/// and the listing's watcher count change together or not at all, and only when the row did.
/// </summary>
public sealed class FakeWatchlistRepository(FakeListingRepository listings) : FakeAdapter, IWatchlistRepository
{
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _lists =
        new(StringComparer.Ordinal);

    public async Task<WatchChange> AddAsync(
        string userId, string listingId, DateTimeOffset savedAt, CancellationToken ct)
    {
        ThrowIfFailing();

        if (_lists.TryGetValue(userId, out var existing) && existing.ContainsKey(listingId))
        {
            return WatchChange.Unchanged;
        }

        var listing = await listings.FindAsync(listingId, ct);
        if (listing is null || listing.Status != ListingStatus.Active)
        {
            return WatchChange.ListingUnavailable;
        }

        listings.TryAdjustWatcherCount(listingId, 1);

        if (existing is null)
        {
            existing = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            _lists[userId] = existing;
        }

        existing[listingId] = savedAt;
        return WatchChange.Changed;
    }

    public Task<WatchChange> RemoveAsync(string userId, string listingId, CancellationToken ct)
    {
        ThrowIfFailing();

        if (!_lists.TryGetValue(userId, out var existing) || !existing.Remove(listingId))
        {
            return Task.FromResult(WatchChange.Unchanged);
        }

        // Same as production: a count that cannot go down (listing gone, or already zero)
        // does not stop the row being removed.
        listings.TryAdjustWatcherCount(listingId, -1);
        return Task.FromResult(WatchChange.Changed);
    }

    public Task<IReadOnlyList<WatchEntry>> ListAsync(string userId, CancellationToken ct)
    {
        ThrowIfFailing();

        IReadOnlyList<WatchEntry> entries = _lists.TryGetValue(userId, out var stored)
            ? stored
                .Select(pair => new WatchEntry(pair.Key, pair.Value))
                .OrderByDescending(entry => entry.SavedAt)
                .ThenByDescending(entry => entry.ListingId, StringComparer.Ordinal)
                .ToList()
            : [];

        return Task.FromResult(entries);
    }

    /// <summary>Plants a saved row without touching the listing's count, for cap and cleanup tests.</summary>
    public void Seed(string userId, string listingId, DateTimeOffset savedAt)
    {
        if (!_lists.TryGetValue(userId, out var existing))
        {
            existing = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            _lists[userId] = existing;
        }

        existing[listingId] = savedAt;
    }
}
