namespace Koleksi.Application.Abstractions;

/// <summary>
/// Finds listings for the header search box as the visitor types.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="IListingSearchIndex"/> on purpose. The results page filters a
/// whole category partition and may read many rows to fill a page; a keystroke must cost one
/// bounded read. The two therefore use different storage shapes, and a different implementation
/// (a search engine, say) can replace either one alone.
/// </para>
/// <para>
/// The rows behind this are written by <see cref="IListingSearchIndex.IndexAsync"/> and removed by
/// <see cref="IListingSearchIndex.RemoveAsync"/>, in the same code path, so there is no second place
/// to forget when a listing changes.
/// </para>
/// </remarks>
public interface IListingSuggestionIndex
{
    /// <summary>
    /// Returns live listings whose title matches every typed word (the last one as a prefix),
    /// best match first, plus the words the visitor could be about to type.
    /// </summary>
    Task<SuggestionMatches> FindAsync(SuggestionLookup lookup, CancellationToken ct);

    /// <summary>
    /// Writes the suggestion rows for listings that were indexed before suggestions existed.
    /// </summary>
    /// <remarks>
    /// Bounded and resumable: it reads at most <paramref name="limit"/> listings of one category and
    /// returns the cursor to continue from, or <c>null</c> when every category has been covered.
    /// Running it again is harmless.
    /// </remarks>
    Task<SuggestionBackfillPage> BackfillAsync(string? cursor, int limit, CancellationToken ct);
}

/// <summary>What to look up.</summary>
/// <param name="Words">The normalised words the visitor typed, in order. The last is the one still being typed.</param>
/// <param name="SupplementCategoryIds">
/// Categories the typed text names. When the title matches fill fewer rows than <paramref name="MaxItems"/>,
/// the newest listings of these categories fill the rest, so typing a category name shows its things.
/// </param>
/// <param name="MaxItems">The most listings to return.</param>
/// <param name="MaxTerms">The most completed words to return.</param>
public sealed record SuggestionLookup(
    IReadOnlyList<string> Words,
    IReadOnlyList<string> SupplementCategoryIds,
    int MaxItems,
    int MaxTerms);

/// <summary>What a lookup found.</summary>
/// <param name="Items">Live listings, in the order they should be shown.</param>
/// <param name="Terms">Complete search phrases, e.g. <c>charizard</c> for the typed <c>cha</c>.</param>
public sealed record SuggestionMatches(IReadOnlyList<ListingSummary> Items, IReadOnlyList<string> Terms)
{
    public static SuggestionMatches None { get; } = new([], []);
}

/// <summary>One step of the suggestion backfill.</summary>
/// <param name="Indexed">Listings written in this step.</param>
/// <param name="NextCursor">Pass back to continue; <c>null</c> when everything has been covered.</param>
public sealed record SuggestionBackfillPage(int Indexed, string? NextCursor);
