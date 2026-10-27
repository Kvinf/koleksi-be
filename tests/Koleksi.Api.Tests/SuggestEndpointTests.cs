using System.Net;
using System.Text.Json;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Catalog;
using Koleksi.Domain.Catalog;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Search suggestions: what an anonymous caller types, and what comes back.
/// </summary>
public sealed class SuggestEndpointTests : IClassFixture<KoleksiApiFactory>
{
    private const string SellerEmail = "penjual.rahasia@example.com";

    private readonly KoleksiApiFactory _factory;

    public SuggestEndpointTests(KoleksiApiFactory factory)
    {
        _factory = factory;

        _factory.Suggestions.Seed(
            Summary("01JQ8ZC4K7N2M5V9XR3P6TW001", "PSA 10 Charizard Base Set", 12_500_000, ListingFormat.FixedPrice),
            Summary("01JQ8ZC4K7N2M5V9XR3P6TW002", "Charizard VMAX Pokémon Card", 450_000, ListingFormat.Auction),
            Summary("01JQ8ZC4K7N2M5V9XR3P6TW003", "Pikachu Pokemon Promo", 90_000, ListingFormat.AcceptOffers),
            Summary("01JQ8ZC4K7N2M5V9XR3P6TW004", "Charizard Sold Out Edition", 1_000_000, ListingFormat.FixedPrice, ListingStatus.Sold),
            Summary("01JQ8ZC4K7N2M5V9XR3P6TW005", "Gunpla RG Nu Gundam", 1_800_000, ListingFormat.FixedPrice, categoryId: "figures", categorySlug: "figur"));
    }

    [Fact]
    public async Task Typing_part_of_a_word_offers_the_word_and_the_listings_that_have_it()
    {
        var body = await SuggestAsync("chari");

        Items(body).Select(item => item.GetProperty("title").GetString())
            .ShouldBe(["Charizard VMAX Pokémon Card", "PSA 10 Charizard Base Set"], ignoreOrder: true);
        Terms(body).ShouldBe(["charizard"]);
    }

    [Fact]
    public async Task An_unaccented_search_finds_an_accented_title()
    {
        var body = await SuggestAsync("pokemon");

        Items(body).Select(item => item.GetProperty("title").GetString())
            .ShouldContain("Charizard VMAX Pokémon Card");
        Items(body).Select(item => item.GetProperty("title").GetString())
            .ShouldContain("Pikachu Pokemon Promo");
        Terms(body).ShouldContain("pokemon");
    }

    [Fact]
    public async Task An_auction_is_suggested_like_any_other_listing()
    {
        var body = await SuggestAsync("vmax");

        var auction = Items(body).ShouldHaveSingleItem();
        auction.GetProperty("format").GetString().ShouldBe("Auction");
        auction.GetProperty("priceRupiah").GetInt64().ShouldBe(450_000);
    }

    [Fact]
    public async Task A_sold_listing_is_never_suggested()
    {
        var body = await SuggestAsync("sold out");

        Items(body).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_typed_word_has_to_match_so_extra_words_narrow_the_list()
    {
        var narrowed = await SuggestAsync("charizard psa");
        var impossible = await SuggestAsync("charizard gundam");

        Items(narrowed).Select(item => item.GetProperty("title").GetString())
            .ShouldBe(["PSA 10 Charizard Base Set"]);
        Items(impossible).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_title_that_begins_with_the_text_comes_before_one_that_only_contains_it()
    {
        var body = await SuggestAsync("charizard");

        Items(body).First().GetProperty("title").GetString().ShouldBe("Charizard VMAX Pokémon Card");
    }

    [Fact]
    public async Task Naming_a_category_shows_the_category_and_tops_up_with_what_is_in_it()
    {
        var body = await SuggestAsync("kartu");

        body.GetProperty("categories").EnumerateArray()
            .Select(category => category.GetProperty("slug").GetString())
            .ShouldContain(TestData.CardsCategorySlug);

        // None of these titles says "kartu"; they appear because the visitor named their category.
        Items(body).ShouldNotBeEmpty();
        Items(body).ShouldAllBe(item => item.GetProperty("categorySlug").GetString() == TestData.CardsCategorySlug);
        _factory.Suggestions.LastLookup.ShouldNotBeNull().SupplementCategoryIds.ShouldBe(["cards"]);
    }

    [Fact]
    public async Task The_answer_carries_only_what_a_row_draws_and_never_who_is_selling()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/catalog/suggest?q=charizard");

        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        raw.ShouldNotContain("@");
        raw.ShouldNotContain(SellerEmail);
        raw.ToLowerInvariant().ShouldNotContain("seller");

        var item = Items(JsonDocument.Parse(raw).RootElement).First();
        // photoKey is left out when a listing has no photo: the API omits null properties.
        item.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "title", "categorySlug", "priceRupiah", "format"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_shared_cache_may_hold_the_answer_for_a_minute()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/catalog/suggest?q=charizard");

        response.Headers.CacheControl.ShouldNotBeNull().Public.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("c")]
    [InlineData("---")]
    [InlineData("a b c d e f g h")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task Text_that_cannot_be_used_is_an_empty_answer_not_an_error(string text)
    {
        var body = await SuggestAsync(text);

        Items(body).ShouldBeEmpty();
        Terms(body).ShouldBeEmpty();
        body.GetProperty("categories").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task No_sign_in_is_needed()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/catalog/suggest?q=gundam");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_storage_failure_is_not_dressed_up_as_no_results()
    {
        // The frontend turns a failure into "just show the full-search row". The API must not
        // pretend it succeeded with an empty list, or an outage would look like an empty catalogue.
        _factory.Suggestions.FailWith = new InvalidOperationException("storage is down");
        try
        {
            using var client = _factory.CreateClient();
            using var response = await client.GetAsync("/api/catalog/suggest?q=outage");

            ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(500);
        }
        finally
        {
            _factory.Suggestions.FailWith = null;
        }
    }

    private async Task<JsonElement> SuggestAsync(string text)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync($"/api/catalog/suggest?q={Uri.EscapeDataString(text)}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadJsonAsync();
    }

    private static List<JsonElement> Items(JsonElement body) => [.. body.GetProperty("items").EnumerateArray()];

    private static List<string?> Terms(JsonElement body) =>
        [.. body.GetProperty("terms").EnumerateArray().Select(term => term.GetString())];

    private static ListingSummary Summary(
        string id,
        string title,
        long price,
        ListingFormat format,
        ListingStatus status = ListingStatus.Active,
        string categoryId = "cards",
        string categorySlug = "kartu") => new(
        Id: id,
        Title: title,
        CategoryId: categoryId,
        CategorySlug: categorySlug,
        PriceRupiah: price,
        Format: format,
        Status: status,
        Condition: "Mint",
        SellerUsername: SellerEmail,
        City: "Jakarta",
        PrimaryPhotoKey: null,
        AuctionEndsAt: null,
        BidCount: 0);
}

/// <summary>The suggestion bucket, in a fixture of its own so no other test spends its tokens.</summary>
public sealed class SuggestRateLimitTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task One_address_hammering_the_box_is_cut_off_and_told_when_to_come_back()
    {
        using var client = factory.CreateClient();

        HttpResponseMessage? throttled = null;
        try
        {
            // Distinct texts, so the shared cache cannot answer instead of the handler.
            for (var attempt = 0; attempt < 90 && throttled is null; attempt++)
            {
                var response = await client.GetAsync($"/api/catalog/suggest?q=gundam{attempt}x");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throttled = response;
                }
                else
                {
                    response.Dispose();
                }
            }

            throttled.ShouldNotBeNull();
            throttled.Headers.RetryAfter.ShouldNotBeNull();
        }
        finally
        {
            throttled?.Dispose();
        }
    }
}
