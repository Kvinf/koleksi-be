namespace Koleksi.Application.Abstractions;

/// <summary>Reads and writes the listings a person has saved ("Simpan").</summary>
/// <remarks>
/// <para>
/// Each save is one row in the person's own partition, and each add or remove also moves the
/// listing's <c>WatcherCount</c> by one, in the same transaction and only when the row really
/// changed. That pairing is why this repository, and not <see cref="IListingRepository"/>,
/// owns the counter: a count that is bumped separately from the row it counts drifts the first
/// time one of the two writes fails, and a repeated tap on a heart would count twice.
/// </para>
/// <para>
/// <c>WatcherCount</c> is deliberately outside the listing's optimistic-concurrency write
/// (see <c>ListingItem.ToItem</c>), so this atomic add is the only thing that can change it.
/// </para>
/// </remarks>
public interface IWatchlistRepository
{
    /// <summary>
    /// Saves a listing, once. Saving one already saved changes nothing, including the count.
    /// </summary>
    /// <remarks>
    /// The listing must still be active at the moment of the write, not merely when the caller
    /// looked: a listing that sells in between is refused here rather than saved and counted.
    /// </remarks>
    Task<WatchChange> AddAsync(string userId, string listingId, DateTimeOffset savedAt, CancellationToken ct);

    /// <summary>
    /// Takes a listing off the list. Works whatever has happened to the listing since: sold,
    /// archived, suspended, or gone altogether.
    /// </summary>
    Task<WatchChange> RemoveAsync(string userId, string listingId, CancellationToken ct);

    /// <summary>Everything the person has saved, newest saved first.</summary>
    Task<IReadOnlyList<WatchEntry>> ListAsync(string userId, CancellationToken ct);
}

/// <summary>What an add or remove did to storage.</summary>
public enum WatchChange
{
    /// <summary>The row was written (or deleted) and the listing's count moved with it.</summary>
    Changed = 0,

    /// <summary>Nothing to do: already saved on an add, already absent on a remove.</summary>
    Unchanged = 1,

    /// <summary>An add found no listing, or one that is no longer active. Nothing was written.</summary>
    ListingUnavailable = 2,
}

/// <summary>One saved listing: a pointer plus when it was saved.</summary>
public sealed record WatchEntry(string ListingId, DateTimeOffset SavedAt);
