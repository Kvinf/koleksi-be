using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Catalog;

/// <summary>
/// Answers the header search box while the visitor is still typing: matching categories, the
/// words they might mean, and live listings, in one list.
/// </summary>
/// <remarks>
/// Public data only. The answer names no seller, no account, and no email: it is built from the
/// browse projection, which already holds nothing else, and then narrowed again to the few fields
/// a suggestion row draws.
/// </remarks>
public sealed class SuggestSearchHandler(IListingSuggestionIndex index, ICategoryRepository categories)
{
    public const int MinWordLength = 2;
    public const int MaxQueryLength = 40;
    public const int MaxWords = 5;
    public const int MaxItems = 5;
    public const int MaxTerms = 3;
    public const int MaxCategories = 2;

    public async Task<SuggestResult> HandleAsync(string? text, CancellationToken ct)
    {
        // Bounded before anything else: the text becomes part of a storage key and a cache key.
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxQueryLength)
        {
            return SuggestResult.None;
        }

        var words = Slug.Words(trimmed);
        if (words.Count == 0 || words.Count > MaxWords || words[^1].Length < MinWordLength)
        {
            return SuggestResult.None;
        }

        var matched = MatchCategories(words, await categories.GetAllAsync(ct).ConfigureAwait(false));

        var found = await index
            .FindAsync(
                new SuggestionLookup(
                    words,
                    SupplementCategoryIds: [.. matched.Take(1).Select(category => category.Id)],
                    MaxItems,
                    MaxTerms),
                ct)
            .ConfigureAwait(false);

        return new SuggestResult(
            Terms: found.Terms,
            Categories: [.. matched.Take(MaxCategories).Select(category => new CategorySuggestion(category.Slug, category.Name))],
            Items: [.. found.Items.Select(item => new ItemSuggestion(
                item.Id, item.Title, item.CategorySlug, item.PriceRupiah, item.Format, item.PrimaryPhotoKey))]);
    }

    /// <summary>Categories where every typed word starts one of the category's words.</summary>
    private static List<Category> MatchCategories(IReadOnlyList<string> typed, IReadOnlyList<Category> all)
    {
        var matches = new List<Category>();

        foreach (var category in all)
        {
            var vocabulary = Slug.Words(category.Name.Id)
                .Concat(Slug.Words(category.Name.En))
                .Concat(Slug.Words(category.Slug))
                .ToList();

            if (SuggestionRanking.StartsAll(typed, vocabulary))
            {
                matches.Add(category);
            }
        }

        return matches;
    }
}

/// <summary>Pure matching and ordering, kept free of storage so it can be tested exhaustively.</summary>
public static class SuggestionRanking
{
    /// <summary>
    /// Whether every typed word starts some word of the title. Typing <c>psa 1</c> matches
    /// "PSA 10 Charizard"; typing <c>charizard psa</c> matches it too, because order does not matter.
    /// </summary>
    public static bool StartsAll(IReadOnlyList<string> typed, IReadOnlyList<string> titleWords)
    {
        foreach (var word in typed)
        {
            var found = false;
            foreach (var titleWord in titleWords)
            {
                if (titleWord.StartsWith(word, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The candidates that match, best first: titles that begin with what was typed, then the
    /// rest, newest first within each. Ids are time-ordered, so a descending id is newest.
    /// </summary>
    public static IReadOnlyList<SuggestionCandidate> Rank(
        IReadOnlyList<string> typed, IEnumerable<SuggestionCandidate> candidates, int take)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(candidates);

        var phrase = string.Join(' ', typed);

        return [.. candidates
            .Where(candidate => StartsAll(typed, candidate.Words))
            .DistinctBy(candidate => candidate.Id, StringComparer.Ordinal)
            .OrderBy(candidate => string.Join(' ', candidate.Words).StartsWith(phrase, StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(candidate => candidate.Id, StringComparer.Ordinal)
            .Take(take)];
    }

    /// <summary>
    /// Words the visitor could be about to type: title words that start with the last typed word,
    /// most common first. Earlier typed words are kept in front, so <c>psa cha</c> offers
    /// <c>psa charizard</c>.
    /// </summary>
    public static IReadOnlyList<string> Terms(
        IReadOnlyList<string> typed, IEnumerable<SuggestionCandidate> candidates, int take)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(candidates);

        var last = typed[^1];
        var lead = typed.Count > 1 ? string.Join(' ', typed.Take(typed.Count - 1)) + " " : string.Empty;

        return [.. candidates
            .Where(candidate => StartsAll(typed, candidate.Words))
            .SelectMany(candidate => candidate.Words.Where(word => word.StartsWith(last, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
            .GroupBy(word => word, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(group => lead + group.Key)];
    }
}

/// <summary>A stored title, split into the same words the typed text is split into.</summary>
public sealed record SuggestionCandidate(string Id, IReadOnlyList<string> Words);

/// <summary>Everything the search box draws.</summary>
public sealed record SuggestResult(
    IReadOnlyList<string> Terms,
    IReadOnlyList<CategorySuggestion> Categories,
    IReadOnlyList<ItemSuggestion> Items)
{
    public static SuggestResult None { get; } = new([], [], []);
}

/// <summary>A category the typed text names.</summary>
public sealed record CategorySuggestion(string Slug, LocalisedText Name);

/// <summary>One live listing; deliberately only what a suggestion row draws.</summary>
public sealed record ItemSuggestion(
    string Id,
    string Title,
    string CategorySlug,
    long PriceRupiah,
    ListingFormat Format,
    string? PhotoKey);
