using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Catalog;

namespace Koleksi.Application.Features.Catalog;

/// <summary>
/// Serves the browse and search results page, with the per-category filter sidebar.
/// </summary>
public sealed class BrowseListingsHandler(
    IListingSearchIndex index,
    ICategoryRepository categories,
    IListingRepository listings,
    PublicIdentityResolver identities)
{
    public async Task<BrowseResult> HandleAsync(BrowseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var allCategories = await categories.GetAllAsync(ct).ConfigureAwait(false);

        // The URL carries a slug ("kartu"); everything downstream keys off the id.
        Category? category = null;
        if (request.CategorySlug is { Length: > 0 } slug)
        {
            category = allCategories.FirstOrDefault(c =>
                string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));

            // An unknown slug is a dead URL, not an empty search. Saying so lets the
            // endpoint return 404 so search engines drop it rather than indexing a
            // "no results" page for every typo anyone ever links.
            if (category is null)
            {
                return BrowseResult.CategoryNotFound;
            }
        }

        var query = BuildQuery(request, category);

        // Results and facet counts are independent reads; issuing them together halves
        // the latency of the page that gets the most traffic on the site.
        var resultsTask = index.SearchAsync(query, ct);
        var facetsTask = index.CountFacetsAsync(query, ct);
        await Task.WhenAll(resultsTask, facetsTask).ConfigureAwait(false);

        var counts = await facetsTask.ConfigureAwait(false);
        var page = await resultsTask.ConfigureAwait(false);

        return new BrowseResult(
            Found: true,
            Page: page with { Items = await WithPublicSellerNamesAsync(page.Items, ct).ConfigureAwait(false) },
            Category: category,
            Categories: allCategories,
            Facets: BuildFacetView(category, counts, query.SelectedFacets),
            AppliedFilters: DescribeAppliedFilters(category, query));
    }

    /// <summary>
    /// Replaces the seller name stored in each browse row with the seller's public handle.
    /// </summary>
    /// <remarks>
    /// Rows indexed before the projection carried a seller id have only the sign-in name, which
    /// for an email account is the email address. Their seller id is read from the listing in one
    /// batch, so old rows stop leaking without being re-indexed. The stored name is never shown.
    /// </remarks>
    private async Task<IReadOnlyList<ListingSummary>> WithPublicSellerNamesAsync(
        IReadOnlyList<ListingSummary> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var unknown = items.Where(item => item.SellerId is null).Select(item => item.Id).ToList();
        var legacy = unknown.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(
                await listings.FindSellerIdsAsync(unknown, ct).ConfigureAwait(false), StringComparer.Ordinal);

        string? SellerOf(ListingSummary item) => item.SellerId ?? legacy.GetValueOrDefault(item.Id);

        var names = await identities
            .ResolveManyAsync(items.Select(SellerOf).OfType<string>(), ct).ConfigureAwait(false);

        // A listing whose seller cannot be found at all (deleted while the row lingered) shows no
        // name rather than the stale one.
        return [.. items.Select(item => item with
        {
            SellerUsername = SellerOf(item) is { } seller ? names[seller].Handle : string.Empty,
            SellerId = null,
        })];
    }

    /// <summary>
    /// Normalises untrusted query-string input into a bounded query.
    /// </summary>
    /// <remarks>
    /// Clamping rather than rejecting: a crawler following a stale <c>?page=9999</c>
    /// link should get the last real page, not a 400 that makes the site look broken.
    /// Facet values a caller invented are dropped, so they can never reach an index key.
    /// </remarks>
    private static BrowseQuery BuildQuery(BrowseRequest request, Category? category)
    {
        var selected = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        if (category is not null)
        {
            foreach (var (key, values) in request.Facets)
            {
                var facet = category.Facets.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
                if (facet is null)
                {
                    continue;
                }

                var permitted = values
                    .Where(v => facet.Options.Any(o => string.Equals(o.Value, v, StringComparison.Ordinal)))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (permitted.Count > 0)
                {
                    selected[key] = permitted;
                }
            }
        }

        var (min, max) = NormalisePriceRange(request.MinPriceRupiah, request.MaxPriceRupiah);

        return new BrowseQuery(
            CategoryId: category?.Id,
            SearchTerm: NormaliseSearchTerm(request.SearchTerm),
            SelectedFacets: selected,
            MinPriceRupiah: min,
            MaxPriceRupiah: max,
            City: string.IsNullOrWhiteSpace(request.City) ? null : request.City.Trim(),
            Sort: request.Sort,
            PageNumber: Math.Clamp(request.PageNumber, 1, BrowseQuery.MaxPageNumber),
            PageSize: Math.Clamp(request.PageSize, 1, BrowseQuery.MaxPageSize));
    }

    /// <summary>Swaps a reversed range rather than returning nothing for it.</summary>
    private static (long? Min, long? Max) NormalisePriceRange(long? min, long? max)
    {
        var low = min is > 0 ? min : null;
        var high = max is > 0 ? max : null;

        return low is { } l && high is { } h && l > h ? (high, low) : (low, high);
    }

    private static string? NormaliseSearchTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        // Capped because the term reaches the search backend. Length limits are the
        // cheapest defence against a pathological query costing far more than it should.
        const int maxLength = 120;
        var trimmed = term.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    /// <summary>
    /// Merges the category's filter schema with live counts, so every option renders
    /// with a number beside it as the design requires.
    /// </summary>
    private static IReadOnlyList<FacetView> BuildFacetView(
        Category? category,
        IReadOnlyList<FacetCount> counts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> selected)
    {
        if (category is null)
        {
            return [];
        }

        var byKey = counts
            .GroupBy(c => c.FacetKey, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(c => c.Value, c => c.Count, StringComparer.Ordinal),
                StringComparer.Ordinal);

        return [.. category.Facets.Select(facet =>
        {
            var facetCounts = byKey.GetValueOrDefault(facet.Key);
            var chosen = selected.GetValueOrDefault(facet.Key) ?? [];

            return new FacetView(
                facet.Key,
                facet.Label,
                [.. facet.Options.Select(option => new FacetOptionView(
                    option.Value,
                    option.Label,
                    facetCounts?.GetValueOrDefault(option.Value) ?? 0,
                    chosen.Contains(option.Value, StringComparer.Ordinal)))]);
        })];
    }

    /// <summary>Builds the removable filter chips shown above the grid.</summary>
    private static List<AppliedFilter> DescribeAppliedFilters(Category? category, BrowseQuery query)
    {
        var chips = new List<AppliedFilter>();

        foreach (var (key, values) in query.SelectedFacets)
        {
            var facet = category?.Facets.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
            foreach (var value in values)
            {
                var label = facet?.Options
                    .FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.Ordinal))?.Label
                    ?? LocalisedText.Same(value);

                chips.Add(new AppliedFilter(key, value, label));
            }
        }

        if (query.City is { } city)
        {
            chips.Add(new AppliedFilter("city", city, LocalisedText.Same(city)));
        }

        return chips;
    }
}

/// <summary>Browse request as parsed from the query string.</summary>
public sealed record BrowseRequest(
    string? CategorySlug,
    string? SearchTerm,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Facets,
    long? MinPriceRupiah,
    long? MaxPriceRupiah,
    string? City,
    BrowseSort Sort,
    int PageNumber,
    int PageSize);

/// <summary>Everything the browse page renders.</summary>
/// <param name="Found"><c>false</c> when the category slug does not exist, so the endpoint can 404.</param>
public sealed record BrowseResult(
    bool Found,
    NumberedPage<ListingSummary> Page,
    Category? Category,
    IReadOnlyList<Category> Categories,
    IReadOnlyList<FacetView> Facets,
    IReadOnlyList<AppliedFilter> AppliedFilters)
{
    public static BrowseResult CategoryNotFound { get; } =
        new(false, NumberedPage.Empty<ListingSummary>(BrowseQuery.DefaultPageSize), null, [], [], []);
}

/// <summary>One filter group ready to render, with counts and selection state.</summary>
public sealed record FacetView(string Key, LocalisedText Label, IReadOnlyList<FacetOptionView> Options);

/// <summary>One filter option with its live count.</summary>
public sealed record FacetOptionView(string Value, LocalisedText Label, long Count, bool IsSelected);

/// <summary>A removable filter chip.</summary>
public sealed record AppliedFilter(string FacetKey, string Value, LocalisedText Label);
