using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IListingSearchIndex"/> that records the query it was given.
/// </summary>
/// <remarks>
/// What matters about browse is not that DynamoDB returns rows — that belongs to the
/// infrastructure tests — but that the untrusted query string is normalised before it
/// reaches storage. Recording the <see cref="BrowseQuery"/> is what lets a test assert
/// that a page number of 9999 was clamped and that a facet value the caller invented
/// never arrived at all.
/// </remarks>
public sealed class FakeListingSearchIndex : FakeAdapter, IListingSearchIndex
{
    private readonly List<ListingSummary> _items = [];
    private readonly List<FacetCount> _facetCounts = [];

    /// <summary>The last query the handler built, as storage would have received it.</summary>
    public BrowseQuery? LastQuery { get; private set; }

    /// <summary>Listing ids passed to <see cref="IndexAsync"/>, in call order.</summary>
    public List<string> Indexed { get; } = [];

    /// <summary>Listing ids passed to <see cref="RemoveAsync"/>, in call order.</summary>
    public List<string> Removed { get; } = [];

    /// <summary>Every seeded or indexed row, as storage currently holds it.</summary>
    public IReadOnlyList<ListingSummary> Items => _items;

    public void Seed(params ListingSummary[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.AddRange(items);
    }

    public void SeedFacetCount(string facetKey, string value, long count) =>
        _facetCounts.Add(new FacetCount(facetKey, value, count));

    public Task<NumberedPage<ListingSummary>> SearchAsync(BrowseQuery query, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(query);
        LastQuery = query;

        var page = _items
            .Skip((query.PageNumber - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        return Task.FromResult(
            new NumberedPage<ListingSummary>(page, query.PageNumber, query.PageSize, _items.Count));
    }

    public Task<IReadOnlyList<FacetCount>> CountFacetsAsync(BrowseQuery query, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(query);
        LastQuery = query;

        return Task.FromResult<IReadOnlyList<FacetCount>>(_facetCounts);
    }

    public Task IndexAsync(ListingIndexEntry entry, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(entry);
        Indexed.Add(entry.Listing.Id);
        return Task.CompletedTask;
    }

    public Task UpdatePriceAsync(string listingId, long currentPriceRupiah, int bidCount, CancellationToken ct)
    {
        ThrowIfFailing();

        var index = _items.FindIndex(item => item.Id == listingId);
        if (index >= 0)
        {
            _items[index] = _items[index] with { PriceRupiah = currentPriceRupiah, BidCount = bidCount };
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string listingId, CancellationToken ct)
    {
        ThrowIfFailing();
        Removed.Add(listingId);
        return Task.CompletedTask;
    }
}
