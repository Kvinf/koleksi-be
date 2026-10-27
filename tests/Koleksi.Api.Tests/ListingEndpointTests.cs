using System.Net;
using System.Net.Http.Json;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Creating, publishing, and archiving a seller's own listings — the write path that
/// previously did not exist, and the one thing that actually populates browse.
/// </summary>
public sealed class ListingEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static object BaseRequest(
        bool publish, bool withPhoto, string ownerId = TestData.SellerId, string format = "FixedPrice") => new
    {
        categoryId = TestData.CardsCategoryId,
        title = "PSA 10 Charizard Base Set Holo 1999",
        priceRupiah = 2_500_000,
        format,
        condition = "PSA 10",
        city = "Bandung",
        description = "Kondisi mulus, kartu asli.",
        photos = withPhoto
            ? new[] { new { storageKey = TestData.PhotoKey(ownerId, "test-photo"), width = 1200, height = 1200 } }
            : Array.Empty<object>(),
        publish,
    };

    [Fact]
    public async Task Creating_a_draft_does_not_publish_or_index_it()
    {
        using var client = factory.CreateClientAs("01JLISTUSER0000000000DRFT", "seller.draft");

        using var response = await client.PostAsJsonAsync("/api/listings", BaseRequest(publish: false, withPhoto: false));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("published").GetBoolean().ShouldBeFalse();

        var listingId = body.GetProperty("listingId").GetString()!;
        factory.SearchIndex.Indexed.ShouldNotContain(listingId);

        var stored = await factory.Listings.FindAsync(listingId, CancellationToken.None);
        stored.ShouldNotBeNull();
        stored!.Status.ShouldBe(ListingStatus.Draft);
    }

    [Fact]
    public async Task Publishing_a_fixed_price_listing_indexes_it()
    {
        using var client = factory.CreateClientAs("01JLISTUSER0000000000PUB1", "seller.publish");

        using var response = await client.PostAsJsonAsync("/api/listings", BaseRequest(publish: true, withPhoto: true, ownerId: "01JLISTUSER0000000000PUB1"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("published").GetBoolean().ShouldBeTrue();

        var listingId = body.GetProperty("listingId").GetString()!;
        var canonicalPath = body.GetProperty("canonicalPath").GetString() ?? string.Empty;
        canonicalPath.ShouldContain(TestData.CardsCategorySlug);
        canonicalPath.ShouldContain(listingId);

        factory.SearchIndex.Indexed.ShouldContain(listingId);

        var stored = await factory.Listings.FindAsync(listingId, CancellationToken.None);
        stored!.Status.ShouldBe(ListingStatus.Active);
    }

    [Fact]
    public async Task Publishing_an_auction_opens_a_live_auction()
    {
        using var client = factory.CreateClientAs("01JLISTUSER0000000000AUCT", "seller.auction");

        var request = new
        {
            categoryId = TestData.CardsCategoryId,
            title = "PSA 10 Charizard Base Set Holo 1999",
            priceRupiah = 1_000_000,
            format = "Auction",
            condition = "PSA 10",
            city = "Bandung",
            description = "Lelang kartu langka.",
            photos = new[] { new { storageKey = TestData.PhotoKey("01JLISTUSER0000000000AUCT", "auction-photo"), width = 1200, height = 1200 } },
            auctionDurationDays = 3,
            publish = true,
        };

        using var response = await client.PostAsJsonAsync("/api/listings", request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listingId = (await response.ReadJsonAsync()).GetProperty("listingId").GetString()!;

        var auction = await factory.Auctions.FindAsync(listingId, CancellationToken.None);
        auction.ShouldNotBeNull();
        auction!.Status.ShouldBe(AuctionStatus.Live);
        auction.StartingBid.Rupiah.ShouldBe(1_000_000L);
    }

    [Fact]
    public async Task Publishing_with_no_photos_is_refused()
    {
        using var client = factory.CreateClientAs("01JLISTUSER0000000000NOPH", "seller.nophoto");

        using var response = await client.PostAsJsonAsync("/api/listings", BaseRequest(publish: true, withPhoto: false));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.photo_required");
    }

    [Fact]
    public async Task A_refused_publish_leaves_nothing_behind()
    {
        // Found by QA: the listing used to be saved as a draft first and published second, so every
        // refused publish left a draft in the seller's dashboard.
        const string sellerId = "01JLISTUSER0000000000NODR";
        using var client = factory.CreateClientAs(sellerId, "seller.nodraft");

        using var response = await client.PostAsJsonAsync("/api/listings", BaseRequest(publish: true, withPhoto: false));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await factory.Listings.CountBySellerAsync(sellerId, ListingStatus.Draft, CancellationToken.None)).ShouldBe(0);
        (await factory.Listings.CountBySellerAsync(sellerId, ListingStatus.Active, CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task An_auction_whose_terms_are_refused_leaves_nothing_behind()
    {
        const string sellerId = "01JLISTUSER0000000000BADT";
        using var client = factory.CreateClientAs(sellerId, "seller.badterms");

        var request = new
        {
            categoryId = TestData.CardsCategoryId,
            title = "PSA 10 Charizard Base Set Holo 1999",
            priceRupiah = 1_000_000,
            format = "Auction",
            condition = "PSA 10",
            city = "Bandung",
            description = "Lelang kartu langka.",
            photos = new[] { new { storageKey = TestData.PhotoKey(sellerId, "terms-photo"), width = 1200, height = 1200 } },
            auctionDurationDays = 3,

            // A reserve above the buyout breaks an auction rule, so the publish must be refused.
            reservePriceRupiah = 5_000_000,
            buyoutPriceRupiah = 2_000_000,
            publish = true,
        };

        using var response = await client.PostAsJsonAsync("/api/listings", request);

        ((int)response.StatusCode).ShouldBeInRange(400, 499);
        (await factory.Listings.CountBySellerAsync(sellerId, ListingStatus.Draft, CancellationToken.None)).ShouldBe(0);
        (await factory.Listings.CountBySellerAsync(sellerId, ListingStatus.Active, CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task A_photo_that_was_never_uploaded_is_refused()
    {
        factory.Media.NextVerifyUploadedResult = false;

        try
        {
            using var client = factory.CreateClientAs("01JLISTUSER0000000000BADP", "seller.badphoto");

            using var response = await client.PostAsJsonAsync("/api/listings", BaseRequest(publish: false, withPhoto: true, ownerId: "01JLISTUSER0000000000BADP"));

            // Same coarse mapping as every other domain-rule violation in this codebase —
            // DomainExceptionHandler maps a plain DomainException to 409, not 400.
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.photo_not_uploaded");
        }
        finally
        {
            factory.Media.NextVerifyUploadedResult = true;
        }
    }

    [Fact]
    public async Task A_repeated_idempotency_key_replays_the_first_create_rather_than_making_two()
    {
        using var client = factory.CreateClientAs("01JLISTUSER0000000000IDEM", "seller.idem");
        var key = Guid.NewGuid().ToString();

        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/listings")
        {
            Content = JsonContent.Create(BaseRequest(publish: false, withPhoto: false)),
        };
        first.Headers.Add("Idempotency-Key", key);
        using var firstResponse = await client.SendAsync(first);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstId = (await firstResponse.ReadJsonAsync()).GetProperty("listingId").GetString();

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/listings")
        {
            Content = JsonContent.Create(BaseRequest(publish: false, withPhoto: false)),
        };
        second.Headers.Add("Idempotency-Key", key);
        using var secondResponse = await client.SendAsync(second);

        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await secondResponse.ReadJsonAsync()).GetProperty("listingId").GetString().ShouldBe(firstId);
    }

    [Fact]
    public async Task Archiving_by_the_owner_removes_it_from_the_index()
    {
        var listing = TestData.ActiveListing();
        factory.Listings.Seed(listing);

        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.PostAsync($"/api/listings/{listing.Id}/archive", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("Archived");
        factory.SearchIndex.Removed.ShouldContain(listing.Id);
    }

    [Fact]
    public async Task Archiving_someone_elses_listing_is_refused()
    {
        var listing = TestData.ActiveListing("A listing that belongs to someone else");
        factory.Listings.Seed(listing);

        using var client = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var response = await client.PostAsync($"/api/listings/{listing.Id}/archive", null);

        // DomainExceptionHandler maps any *.forbidden code to 403, not the usual 409.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.forbidden");
    }

    [Fact]
    public async Task Mine_returns_only_the_callers_own_listings_with_real_counts()
    {
        var mine = TestData.ActiveListing("My own listing");
        mine.ApplyViewCount(42);
        mine.ApplyWatcherCount(5);
        factory.Listings.Seed(mine);

        // TestData.ActiveListing always seller TestData.SellerId, so "someone else's"
        // listing is built directly here, owned by a different subject.
        var someoneElses = Listing.CreateDraft(
            TestData.RivalId,
            TestData.CardsCategoryId,
            "A listing that is not mine at all",
            Money.FromRupiah(1_000_000),
            ListingFormat.FixedPrice,
            "PSA 10",
            "Bandung",
            factory.Clock);
        someoneElses.SetPhotos([ListingPhoto.Create("listings/other.jpg", 800, 800)], factory.Clock);
        someoneElses.Publish(factory.Clock);
        factory.Listings.Seed(someoneElses);

        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.GetAsync("/api/listings/mine");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await response.ReadJsonAsync()).GetProperty("items").EnumerateArray().ToList();

        // Not an exact count: the shared test-class fixture means other tests in this
        // file may have already seeded listings under TestData.SellerId. What matters
        // here is that this test's own listing is present with real counts, and that
        // the other seller's listing never appears at all.
        items.ShouldContain(item => item.GetProperty("id").GetString() == mine.Id);
        items.ShouldNotContain(item => item.GetProperty("id").GetString() == someoneElses.Id);

        var withCounts = items.First(item => item.GetProperty("id").GetString() == mine.Id);
        withCounts.GetProperty("viewCount").GetInt64().ShouldBe(42L);
        withCounts.GetProperty("watcherCount").GetInt64().ShouldBe(5L);
    }
}
