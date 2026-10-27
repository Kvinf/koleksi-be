using Koleksi.Domain.Common;

namespace Koleksi.Domain.Catalog;

/// <summary>
/// What may be saved to a watchlist ("Simpan"), and how many a person may keep.
/// </summary>
/// <remarks>
/// Pure functions, so the rules are tested without a handler or a table. Removing is not
/// covered here on purpose: taking a listing off the list must work whatever happened to the
/// listing since (sold, archived, suspended), so it has no precondition to enforce.
/// </remarks>
public static class WatchlistRules
{
    /// <summary>
    /// The most listings one person can save.
    /// </summary>
    /// <remarks>
    /// Every read of the list costs one query for the whole partition, so without a ceiling a
    /// script could grow its own list until each page load pays for it. Two hundred is far more
    /// than a collector browsing for a week will actually tick.
    /// </remarks>
    public const int MaxItems = 200;

    /// <summary>
    /// Refuses a save that breaks a rule. Returns normally when the listing may be added.
    /// </summary>
    /// <param name="alreadySaved">
    /// Whether this listing is on the list already. Saving it again changes nothing, so it is
    /// allowed even at the ceiling and even if the listing has since stopped being active.
    /// </param>
    /// <param name="currentCount">How many listings the person has saved now.</param>
    /// <exception cref="DomainException">
    /// <c>watchlist.own_listing</c>, <c>watchlist.not_active</c> or <c>watchlist.limit_reached</c>.
    /// </exception>
    public static void EnsureCanSave(Listing listing, string userId, bool alreadySaved, int currentCount)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (alreadySaved)
        {
            return;
        }

        // A seller watching their own item would inflate the very number buyers read as
        // interest in it.
        if (string.Equals(listing.SellerId, userId, StringComparison.Ordinal))
        {
            throw DomainException.For("watchlist.own_listing", "You cannot save your own listing.");
        }

        if (listing.Status != ListingStatus.Active)
        {
            throw DomainException.For(
                "watchlist.not_active", "Only a listing that is for sale can be saved.");
        }

        if (currentCount >= MaxItems)
        {
            throw DomainException.For(
                "watchlist.limit_reached", $"You can save at most {MaxItems} listings.");
        }
    }
}
