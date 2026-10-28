using Koleksi.Api.Security;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// Public catalogue reads: browse, search, categories, and the item page.
/// </summary>
/// <remarks>
/// <para>
/// Every route here is <c>AllowAnonymous</c>, which is a decision rather than an
/// oversight: the handoff requires item and auction pages to be crawlable, and a crawler
/// carries no token. The application's fallback policy demands authentication everywhere
/// else, so these opt-outs are the complete list of what an anonymous caller can reach.
/// </para>
/// <para>
/// They are also the only routes with a shared cache. Nothing returned here varies by
/// caller — the item handler decides visibility from the caller's identity, which is why
/// the item route is <b>not</b> output-cached even though it looks like the best candidate.
/// </para>
/// </remarks>
internal static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/catalog")
            .AllowAnonymous()
            .WithTags("Catalog");

        group.MapGet("/categories", GetCategoriesAsync)
            .CacheOutput(policy => policy.Expire(TimeSpan.FromMinutes(10)))
            .WithName("GetCategories");

        group.MapGet("/browse", BrowseAsync)
            .WithName("BrowseListings");

        // Identical for every caller, so a shared cache may hold each distinct text for a minute.
        group.MapGet("/suggest", SuggestAsync)
            .RequireRateLimiting(RateLimitPolicies.Suggest)
            .CacheOutput(policy => policy.Expire(TimeSpan.FromSeconds(60)).SetVaryByQuery("q"))
            .WithName("SuggestSearch");

        group.MapGet("/listings/{slugWithId}", GetListingAsync)
            .WithName("GetListing");

        return app;
    }

    /// <summary>
    /// Autocomplete for the header search box: categories, completed words and live listings.
    /// </summary>
    /// <remarks>
    /// Public data only, and deliberately narrow: no seller, no account id, nothing beyond what a
    /// suggestion row draws. Text the handler cannot use (too short, too long, no letters) answers
    /// with empty lists rather than an error, because the box fires on every pause in typing.
    /// </remarks>
    private static async Task<IResult> SuggestAsync(
        HttpContext context,
        SuggestSearchHandler handler,
        IMediaStorage media,
        [FromQuery(Name = "q")] string? text,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(text, ct).ConfigureAwait(false);

        context.Response.Headers.CacheControl = "public, max-age=60";

        return Results.Ok(new
        {
            terms = result.Terms,
            categories = result.Categories.Select(category => new
            {
                category.Slug,
                name = new { category.Name.Id, category.Name.En },
            }),
            items = result.Items.Select(item => new
            {
                item.Id,
                item.Title,
                item.CategorySlug,
                item.PriceRupiah,
                format = item.Format.ToString(),
                item.PhotoKey,
                PhotoUrl = media.OriginalUrl(item.PhotoKey),
            }),
        });
    }

    private static async Task<IResult> GetCategoriesAsync(
        ICategoryRepository categories, CancellationToken ct)
    {
        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);

        return Results.Ok(all.Select(category => new
        {
            category.Id,
            category.Slug,
            Name = new { category.Name.Id, category.Name.En },
            Facets = category.Facets.Select(facet => new
            {
                facet.Key,
                Label = new { facet.Label.Id, facet.Label.En },
                Options = facet.Options.Select(option => new
                {
                    option.Value,
                    Label = new { option.Label.Id, option.Label.En },
                }),
            }),
        }));
    }

    /// <summary>
    /// Browse and search.
    /// </summary>
    /// <remarks>
    /// Facets arrive as repeated <c>f.{key}=value</c> pairs, e.g.
    /// <c>?f.grade=PSA+10&amp;f.grade=PSA+9&amp;f.set=Base+Set</c>. Every key and value is
    /// checked against the category's schema in the handler and silently dropped if it is
    /// not there, so nothing a caller invents reaches an index key.
    /// </remarks>
    private static async Task<IResult> BrowseAsync(
        HttpContext context,
        BrowseListingsHandler handler,
        IMediaStorage media,
        [FromQuery(Name = "kategori")] string? categorySlug,
        [FromQuery(Name = "q")] string? searchTerm,
        [FromQuery(Name = "kota")] string? city,
        [FromQuery(Name = "min")] long? minPrice,
        [FromQuery(Name = "max")] long? maxPrice,
        [FromQuery(Name = "urut")] string? sort,
        [FromQuery(Name = "page")] int? page,
        [FromQuery(Name = "size")] int? size,
        CancellationToken ct)
    {
        var request = new BrowseRequest(
            CategorySlug: categorySlug,
            SearchTerm: searchTerm,
            Facets: ReadFacets(context.Request.Query),
            MinPriceRupiah: minPrice,
            MaxPriceRupiah: maxPrice,
            City: city,
            Sort: ParseSort(sort),

            // The handler clamps rather than rejects, so a crawler following a stale
            // "?page=9999" link lands on the last real page instead of a 400.
            PageNumber: page ?? 1,
            PageSize: size ?? BrowseQuery.DefaultPageSize);

        var result = await handler.HandleAsync(request, ct).ConfigureAwait(false);

        if (!result.Found)
        {
            // An unknown category slug is a dead URL. Answering 404 keeps search engines
            // from indexing a "no results" page for every typo anyone ever links to.
            return Results.NotFound(new { code = "category.not_found" });
        }

        // Public and identical for every caller, so a shared cache may hold it briefly.
        // Short, because a newly published listing appearing a minute late is acceptable
        // and an hour late is not.
        context.Response.Headers.CacheControl = "public, max-age=60";

        return Results.Ok(new
        {
            items = result.Page.Items.Select(item => item.ToResponse(media)),
            page = new
            {
                number = result.Page.PageNumber,
                size = result.Page.PageSize,
                total = result.Page.TotalCount,
                totalPages = result.Page.TotalPages,
                hasNext = result.Page.HasNext,
                hasPrevious = result.Page.HasPrevious,
            },
            category = result.Category is null ? null : new
            {
                result.Category.Id,
                result.Category.Slug,
                name = new { result.Category.Name.Id, result.Category.Name.En },
            },
            facets = result.Facets.Select(facet => new
            {
                facet.Key,
                label = new { facet.Label.Id, facet.Label.En },
                options = facet.Options.Select(option => new
                {
                    option.Value,
                    label = new { option.Label.Id, option.Label.En },
                    option.Count,
                    option.IsSelected,
                }),
            }),
            appliedFilters = result.AppliedFilters.Select(filter => new
            {
                filter.FacetKey,
                filter.Value,
                label = new { filter.Label.Id, filter.Label.En },
            }),
        });
    }

    private static async Task<IResult> GetListingAsync(
        string slugWithId,
        GetListingHandler handler,
        IListingRepository listings,
        CancellationToken ct)
    {
        var result = await handler
            .HandleAsync(new GetListingQuery(slugWithId), ct)
            .ConfigureAwait(false);

        if (!result.Found || result.Listing is not { } listing)
        {
            return Results.NotFound(new { code = "listing.not_found" });
        }

        // Fire-and-forget would be wrong here — an unawaited task on Lambda can be frozen
        // mid-flight when the invocation ends. It is a single atomic ADD and cheap enough
        // to await; a failure inside it is already swallowed.
        await listings.IncrementViewCountAsync(listing.Id, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            listing.Id,
            listing.Title,
            listing.Description,
            priceRupiah = listing.Price.Rupiah,
            format = listing.Format.ToString(),
            status = listing.Status.ToString(),
            listing.Condition,
            listing.City,
            listing.CategoryId,
            categorySlug = result.Category?.Slug,
            attributes = listing.Attributes,
            photos = result.Photos,
            listing.ViewCount,
            listing.WatcherCount,
            listing.AcceptsOffers,
            publishedAt = listing.PublishedAt,
            canonicalPath = result.CanonicalPath,
            result.IsOwnListing,

            // Handle and chosen display name only; see PublicIdentityResolver.
            seller = result.Seller,
            auction = result.Auction,

            // Units on offer and how many are free right now; absent for an auction (always one lot).
            stock = result.Stock,
        });
    }

    /// <summary>Reads the repeated <c>f.{key}</c> facet parameters out of the query string.</summary>
    private static Dictionary<string, IReadOnlyList<string>> ReadFacets(IQueryCollection query)
    {
        const string prefix = "f.";
        const int maxFacets = 12;
        const int maxValuesPerFacet = 24;

        var facets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (key, values) in query)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || facets.Count >= maxFacets)
            {
                continue;
            }

            // Bounded before the handler sees it. Each selected value becomes a clause in a
            // DynamoDB filter expression, and that expression has a size limit — a URL with
            // a thousand repeated parameters would otherwise fail deep in the SDK rather
            // than being ignored here.
            var selected = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Take(maxValuesPerFacet)
                .Select(value => value!)
                .ToList();

            if (selected.Count > 0)
            {
                facets[key[prefix.Length..]] = selected;
            }
        }

        return facets;
    }

    private static BrowseSort ParseSort(string? sort) => sort switch
    {
        "terbaru" or "newest" => BrowseSort.Newest,
        "termurah" or "price-asc" => BrowseSort.PriceAscending,
        "termahal" or "price-desc" => BrowseSort.PriceDescending,
        "berakhir" or "ending" => BrowseSort.AuctionsEndingSoon,
        _ => BrowseSort.Relevance,
    };
}
