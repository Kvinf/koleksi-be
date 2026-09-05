using System.Net;
using Koleksi.Domain.Catalog;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Who may see which listing.
/// </summary>
/// <remarks>
/// <para>
/// The item route is anonymous, so these rules are the only thing standing between a
/// draft — or a listing moderation has removed — and the open internet. They are decided
/// in <c>GetListingHandler</c> rather than by a filter upstream, and this is where that
/// decision is held to account.
/// </para>
/// <para>
/// Every refusal is a 404, never a 403. A 403 would confirm the listing exists, turning
/// the endpoint into an oracle for which listings a moderator has taken down.
/// </para>
/// </remarks>
public sealed class ListingVisibilityTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task An_active_listing_is_public()
    {
        var listing = Seed(TestData.ActiveListing());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.ReadJsonAsync();
        body.GetProperty("id").GetString().ShouldBe(listing.Id);
        body.GetProperty("canonicalPath").GetString()
            .ShouldBe(listing.UrlPath(TestData.CardsCategorySlug));
    }

    [Fact]
    public async Task A_draft_is_invisible_to_the_public()
    {
        var listing = Seed(TestData.DraftListing());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_seller_can_see_their_own_draft()
    {
        var listing = Seed(TestData.DraftListing());
        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.GetAsync(Path(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("isOwnListing").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Another_signed_in_user_cannot_see_someone_elses_draft()
    {
        var listing = Seed(TestData.DraftListing());
        using var client = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var response = await client.GetAsync(Path(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_suspended_listing_is_hidden_from_its_own_seller()
    {
        // Not an oversight. A seller who can still reach their removed listing will keep
        // linking to it, and a suspension is a moderation decision they cannot browse
        // around — unlike an archive, which is theirs to reverse.
        var listing = Seed(TestData.SuspendedListing());
        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.GetAsync(Path(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Staff_can_see_a_suspended_listing()
    {
        var listing = Seed(TestData.SuspendedListing());
        using var client = factory.CreateClientAs(TestData.StaffId, "moderator.dwi", "staff");

        using var response = await client.GetAsync(Path(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_group_claim_that_merely_contains_staff_does_not_grant_it()
    {
        // Ordinal, whole-value comparison. "staff-applicants" must not read as "staff".
        var listing = Seed(TestData.SuspendedListing());
        using var client = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername, "staff-applicants");

        using var response = await client.GetAsync(Path(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("charizard-00000")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("%2E%2E%2F%2E%2E%2Fkoleksi-prod")]

    // Right length, wrong alphabet: Crockford base-32 has no I, L, O or U, precisely so
    // that a hand-typed id cannot be confused with a neighbouring one.
    [InlineData("listing-01JBIDDERAAAAAAAAAAAAAAAAA")]
    public async Task A_route_segment_that_is_not_an_identifier_never_reaches_storage(string segment)
    {
        using var client = factory.CreateClient();
        var before = factory.Listings.ViewIncrements.Count;

        using var response = await client.GetAsync($"/api/catalog/listings/{segment}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The point is not only the status code. Slug.ExtractId refuses the segment before
        // a lookup happens, which is what keeps a hostile path from becoming a storage key.
        factory.Listings.ViewIncrements.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_stale_slug_still_resolves_and_answers_with_the_canonical_path()
    {
        // Inbound links outlive titles. The id is what resolves; the slug is decoration.
        var listing = Seed(TestData.ActiveListing());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/catalog/listings/an-old-title-{listing.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("canonicalPath").GetString()
            .ShouldBe(listing.UrlPath(TestData.CardsCategorySlug));
    }

    [Fact]
    public async Task Viewing_a_listing_counts_the_view()
    {
        var listing = Seed(TestData.ActiveListing());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Listings.ViewIncrements.ShouldContain(listing.Id);
    }

    [Fact]
    public async Task An_item_response_is_never_stored_by_a_shared_cache()
    {
        // It varies by caller: the same URL returns a draft to its seller and a 404 to
        // everyone else. A shared cache holding one of those answers would serve it to
        // the other.
        var listing = Seed(TestData.ActiveListing());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path(listing));

        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }

    private Listing Seed(Listing listing)
    {
        factory.Listings.Seed(listing);
        return listing;
    }

    private static string Path(Listing listing) =>
        $"/api/catalog/listings/{listing.UrlPath(TestData.CardsCategorySlug).Split('/')[^1]}";
}
