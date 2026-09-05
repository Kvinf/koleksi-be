using System.Net;
using Koleksi.Application.Abstractions;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// How the browse query string is normalised before it reaches storage.
/// </summary>
/// <remarks>
/// Browse takes more untrusted input than anything else in the product — a category slug,
/// free text, a price range, a city, a sort, a page, a size, and an open-ended set of
/// repeated facet parameters — and it takes all of it from anonymous callers. The
/// assertions below are about the <see cref="BrowseQuery"/> that storage was actually
/// handed, because the response body cannot show what was filtered out on the way in.
/// </remarks>
public sealed class BrowseEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task The_category_list_is_served_to_anonymous_callers()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog/categories");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var categories = (await response.ReadJsonAsync()).EnumerateArray().ToList();
        categories.Count.ShouldBe(6);
        categories.ShouldContain(category =>
            category.GetProperty("slug").GetString() == TestData.CardsCategorySlug);
    }

    [Fact]
    public async Task An_unknown_category_slug_is_a_dead_url()
    {
        // Not an empty result set. A "no results" page for every typo anyone ever links
        // to is a page search engines will happily index.
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog/browse?kategori=tidak-ada");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("category.not_found");
    }

    [Fact]
    public async Task A_page_size_a_caller_invented_is_clamped()
    {
        // Without a ceiling, "?size=100000" is a free denial-of-service primitive against
        // both read capacity and the egress bill.
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog/browse?size=100000");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.SearchIndex.LastQuery.ShouldNotBeNull().PageSize.ShouldBe(BrowseQuery.MaxPageSize);
    }

    [Fact]
    public async Task A_page_number_past_the_ceiling_is_clamped_rather_than_rejected()
    {
        // Clamped, not refused: a crawler following a stale deep link should land on the
        // last real page instead of a 400 that makes the site look broken.
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog/browse?page=9999");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.SearchIndex.LastQuery.ShouldNotBeNull().PageNumber.ShouldBe(BrowseQuery.MaxPageNumber);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-5")]
    [InlineData("size=0")]
    [InlineData("size=-1")]
    public async Task Nonsensical_paging_is_normalised_to_something_answerable(string query)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/catalog/browse?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var last = factory.SearchIndex.LastQuery.ShouldNotBeNull();
        last.PageNumber.ShouldBeGreaterThanOrEqualTo(1);
        last.PageSize.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task A_facet_value_the_caller_invented_never_reaches_storage()
    {
        // "PSA 11" does not exist. Accepting it would let a seller mint a grade that sorts
        // above every honest listing, and would push unvetted text into an index key.
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/catalog/browse?kategori=kartu&f.grade=PSA%2010&f.grade=PSA%2011");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var selected = factory.SearchIndex.LastQuery.ShouldNotBeNull().SelectedFacets;
        selected["grade"].ShouldHaveSingleItem().ShouldBe("PSA 10");
    }

    [Fact]
    public async Task A_facet_key_that_is_not_in_the_category_schema_is_dropped()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/catalog/browse?kategori=kartu&f.internal_flag=true&f.grade=PSA%209");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var selected = factory.SearchIndex.LastQuery.ShouldNotBeNull().SelectedFacets;
        selected.ShouldNotContainKey("internal_flag");
        selected.ShouldContainKey("grade");
    }

    [Fact]
    public async Task A_flood_of_facet_parameters_is_absorbed_and_nothing_of_it_survives()
    {
        // Each selected value becomes a clause in a DynamoDB filter expression, and that
        // expression has a size limit. Four hundred repeated parameters are bounded twice
        // — the endpoint reads at most twelve, and the handler keeps only what the
        // category schema defines — so the request answers normally and storage sees none
        // of it.
        var flood = string.Join('&', Enumerable.Range(0, 400).Select(i => $"f.facet{i}=value"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/catalog/browse?kategori=kartu&{flood}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.SearchIndex.LastQuery.ShouldNotBeNull().SelectedFacets.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_very_long_search_term_is_truncated()
    {
        var term = new string('a', 5_000);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/catalog/browse?q={term}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.SearchIndex.LastQuery.ShouldNotBeNull().SearchTerm.ShouldNotBeNull().Length.ShouldBe(120);
    }

    [Fact]
    public async Task A_reversed_price_range_is_swapped_rather_than_returning_nothing()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog/browse?min=5000000&max=1000000");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var last = factory.SearchIndex.LastQuery.ShouldNotBeNull();
        last.MinPriceRupiah.ShouldBe(1_000_000);
        last.MaxPriceRupiah.ShouldBe(5_000_000);
    }

    [Fact]
    public async Task Facet_counts_are_rendered_beside_every_option_in_the_schema()
    {
        factory.SearchIndex.SeedFacetCount("grade", "PSA 10", 42);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog/browse?kategori=kartu");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var grade = (await response.ReadJsonAsync())
            .GetProperty("facets")
            .EnumerateArray()
            .Single(facet => facet.GetProperty("key").GetString() == "grade");

        var options = grade.GetProperty("options").EnumerateArray().ToList();

        // Every option, not only the counted ones: a filter that vanishes when its count
        // reaches zero is a filter a shopper cannot use to get back to where they were.
        options.Count.ShouldBe(4);
        options.Single(option => option.GetProperty("value").GetString() == "PSA 10")
            .GetProperty("count").GetInt64().ShouldBe(42);
    }
}
