using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Catalog;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IListingSuggestionIndex"/> that matches the way the real one does:
/// the same word splitting, the same ranking, and only Active listings.
/// </summary>
/// <remarks>
/// The ranking and word rules are the production code (<see cref="SuggestionRanking"/>,
/// <see cref="Slug.Words"/>), so a handler test here exercises the real decisions; what is faked is
/// only where the rows live.
/// </remarks>
public sealed class FakeListingSuggestionIndex : FakeAdapter, IListingSuggestionIndex
{
    private readonly List<ListingSummary> _items = [];

    /// <summary>The last lookup the handler built, as storage would have received it.</summary>
    public SuggestionLookup? LastLookup { get; private set; }

    public void Seed(params ListingSummary[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.AddRange(items);
    }

    public Task<SuggestionMatches> FindAsync(SuggestionLookup lookup, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(lookup);
        LastLookup = lookup;

        var live = _items.Where(item => item.Status == ListingStatus.Active).ToList();
        var candidates = live
            .Select(item => new SuggestionCandidate(item.Id, Slug.Words(item.Title)))
            .ToList();

        var ranked = SuggestionRanking.Rank(lookup.Words, candidates, lookup.MaxItems);
        var items = ranked.Select(candidate => live.First(item => item.Id == candidate.Id)).ToList();

        foreach (var categoryId in lookup.SupplementCategoryIds)
        {
            foreach (var extra in live.Where(item => item.CategoryId == categoryId).OrderByDescending(item => item.Id, StringComparer.Ordinal))
            {
                if (items.Count < lookup.MaxItems && items.All(item => item.Id != extra.Id))
                {
                    items.Add(extra);
                }
            }
        }

        return Task.FromResult(new SuggestionMatches(
            items, SuggestionRanking.Terms(lookup.Words, candidates, lookup.MaxTerms)));
    }

    public Task<SuggestionBackfillPage> BackfillAsync(string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(new SuggestionBackfillPage(0, null));
    }
}
