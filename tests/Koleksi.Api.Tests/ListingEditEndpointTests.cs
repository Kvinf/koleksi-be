using System.Net;
using System.Net.Http.Json;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Editing a listing that is or was live (PUT /api/listings/{id}), the relaxed draft rules, and
/// paging of the seller's own listings.
/// </summary>
public sealed class ListingEditEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string Title = "PSA 10 Charizard Base Set Holo 1999";

    // A body shaped the way the sell form sends a live listing back: the locked fields echo what
    // is stored, the editable ones carry the change.
    private static object Edit(
        Listing listing,
        long? price = null,
        string description = "Deskripsi baru yang lebih jelas.",
        string condition = "PSA 9",
        string city = "Surabaya",
        string? title = null,
        string? format = null,
        string? categoryId = null,
        object[]? photos = null) => new
    {
        categoryId = categoryId ?? listing.CategoryId,
        title = title ?? listing.Title,
        priceRupiah = price ?? listing.Price.Rupiah,
        format = format ?? listing.Format.ToString(),
        condition,
        city,
        description,
        photos = photos ?? [new { storageKey = TestData.PhotoKey(listing.SellerId, "new-photo"), width = 1200, height = 1200 }],
        publish = false,
    };

    private static string Url(Listing listing) => $"/api/listings/{listing.Id}";

    private Listing SeedActive(ListingFormat format = ListingFormat.FixedPrice)
    {
        var listing = TestData.ActiveListing(Title, Money.FromRupiah(2_500_000), format);
        factory.Listings.Seed(listing);
        return listing;
    }

    private HttpClient Seller() => factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

    // ─── A live listing ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Editing_an_active_listing_saves_the_changes_and_reindexes_it()
    {
        var listing = SeedActive();
        var indexedBefore = factory.SearchIndex.Indexed.Count(id => id == listing.Id);
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing, price: 2_000_000));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("published").GetBoolean().ShouldBeTrue();
        body.GetProperty("canonicalPath").GetString()!.ShouldContain(listing.Id);

        var stored = await factory.Listings.FindAsync(listing.Id, CancellationToken.None);
        stored!.Status.ShouldBe(ListingStatus.Active);
        stored.Price.Rupiah.ShouldBe(2_000_000);
        stored.Description.ShouldBe("Deskripsi baru yang lebih jelas.");
        stored.Condition.ShouldBe("PSA 9");
        stored.City.ShouldBe("Surabaya");
        stored.Photos.Single().StorageKey.ShouldBe(TestData.PhotoKey(listing.SellerId, "new-photo"));
        stored.Title.ShouldBe(Title);

        // The browse projection (title, price, photo, city) is rewritten, not left stale.
        factory.SearchIndex.Indexed.Count(id => id == listing.Id).ShouldBe(indexedBefore + 1);
    }

    [Fact]
    public async Task Editing_an_archived_listing_saves_it_but_does_not_put_it_back_in_browse()
    {
        var listing = SeedActive();
        listing.Archive(factory.Clock);
        factory.Listings.Seed(listing);
        var indexedBefore = factory.SearchIndex.Indexed.Count(id => id == listing.Id);
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("published").GetBoolean().ShouldBeFalse();
        var stored = await factory.Listings.FindAsync(listing.Id, CancellationToken.None);
        stored!.Status.ShouldBe(ListingStatus.Archived);
        stored.Description.ShouldBe("Deskripsi baru yang lebih jelas.");
        factory.SearchIndex.Indexed.Count(id => id == listing.Id).ShouldBe(indexedBefore);
    }

    [Fact]
    public async Task A_photo_already_on_the_listing_is_not_verified_again_but_a_new_one_is()
    {
        var listing = SeedActive();
        var existing = listing.Photos[0].StorageKey;
        using var client = Seller();
        factory.Media.NextVerifyUploadedResult = false;

        try
        {
            using var keep = await client.PutAsJsonAsync(
                Url(listing), Edit(listing, photos: [new { storageKey = existing, width = 1200, height = 1200 }]));
            keep.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var swap = await client.PutAsJsonAsync(Url(listing), Edit(listing));
            swap.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await swap.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.photo_not_uploaded");
        }
        finally
        {
            factory.Media.NextVerifyUploadedResult = true;
        }
    }

    [Fact]
    public async Task A_live_listing_cannot_be_left_without_a_photo()
    {
        var listing = SeedActive();
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing, photos: []));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.photo_required");
        (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!.Photos.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_price_below_the_floor_is_refused_and_nothing_changes()
    {
        var listing = SeedActive();
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing, price: 5_000));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.price_too_low");
        (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!.Price.Rupiah.ShouldBe(2_500_000);
    }

    [Fact]
    public async Task Category_format_and_title_are_locked_after_publish()
    {
        var listing = SeedActive();
        using var client = Seller();

        using var category = await client.PutAsJsonAsync(Url(listing), Edit(listing, categoryId: "sneakers"));
        using var format = await client.PutAsJsonAsync(Url(listing), Edit(listing, format: "Auction"));
        using var title = await client.PutAsJsonAsync(Url(listing), Edit(listing, title: "A completely different title"));

        (await category.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.category_immutable");
        (await format.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.format_immutable");
        (await title.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.title_immutable");
        category.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_pending_offer_keeps_the_asking_price_it_was_made_against_when_the_price_changes()
    {
        var listing = SeedActive(ListingFormat.AcceptOffers);
        var offer = Offer.Create(
            listing.Id, Guid.NewGuid().ToString(), listing.SellerId, Money.FromRupiah(2_000_000), listing.Price,
            "Bisa?", autoDeclineBelowPercent: null, factory.Clock);
        factory.Offers.Seed(offer);
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing, price: 3_000_000));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await factory.Offers.FindAsync(offer.Id, CancellationToken.None);
        stored!.Status.ShouldBe(OfferStatus.Pending);
        stored.AskingPrice.Rupiah.ShouldBe(2_500_000);
        stored.Amount.Rupiah.ShouldBe(2_000_000);
    }

    [Fact]
    public async Task Someone_elses_listing_cannot_be_edited()
    {
        var listing = SeedActive();
        using var rival = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var response = await rival.PutAsJsonAsync(Url(listing), Edit(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.forbidden");
        (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!.City.ShouldBe("Bandung");
    }

    [Fact]
    public async Task A_sold_listing_cannot_be_edited()
    {
        var listing = SeedActive();
        listing.MarkSold(factory.Clock);
        factory.Listings.Seed(listing);
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.not_editable");
    }

    [Fact]
    public async Task A_suspended_listing_cannot_be_edited()
    {
        var listing = SeedActive();
        listing.Suspend(factory.Clock);
        factory.Listings.Seed(listing);
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.not_editable");
    }

    [Fact]
    public async Task A_repeated_idempotency_key_applies_the_edit_once()
    {
        var listing = SeedActive();
        using var client = Seller();
        var key = Guid.NewGuid().ToString();
        var indexedBefore = factory.SearchIndex.Indexed.Count(id => id == listing.Id);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, Url(listing))
            {
                Content = JsonContent.Create(Edit(listing)),
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        factory.SearchIndex.Indexed.Count(id => id == listing.Id).ShouldBe(indexedBefore + 1);
        (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!.Version.ShouldBe(listing.Version + 1);
    }

    // ─── Auctions ────────────────────────────────────────────────────────────────

    private Listing SeedLiveAuction()
    {
        var listing = SeedActive(ListingFormat.Auction);
        factory.Auctions.Seed(TestData.LiveAuction(listing, listing.Price));
        return listing;
    }

    [Fact]
    public async Task An_auctions_starting_price_cannot_be_changed()
    {
        var listing = SeedLiveAuction();
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing, price: 1_000_000));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.auction_price_immutable");
    }

    [Fact]
    public async Task An_auction_without_bids_can_have_its_description_and_photos_edited()
    {
        var listing = SeedLiveAuction();
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!
            .Description.ShouldBe("Deskripsi baru yang lebih jelas.");
    }

    [Fact]
    public async Task An_auction_that_has_bids_cannot_be_edited_and_the_edit_form_says_so()
    {
        var listing = SeedActive(ListingFormat.Auction);
        var auction = TestData.LiveAuction(listing, listing.Price);
        auction.PlaceBid(Guid.NewGuid().ToString(), "first.bidder", Money.FromRupiah(2_600_000), factory.Clock);
        factory.Auctions.Seed(auction);
        using var client = Seller();

        using var response = await client.PutAsJsonAsync(Url(listing), Edit(listing));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.has_bids");
        (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!.City.ShouldBe("Bandung");

        var form = await (await client.GetAsync(Url(listing))).ReadJsonAsync();
        form.GetProperty("auctionHasBids").GetBoolean().ShouldBeTrue();
        form.GetProperty("wasPublished").GetBoolean().ShouldBeTrue();
    }

    // ─── Drafts ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_draft_can_be_saved_with_only_a_title_a_category_and_a_format()
    {
        using var client = factory.CreateClientAs("01JLISTUSER000000000EDRA1", "seller.minimal");

        using var created = await client.PostAsJsonAsync(
            "/api/listings", new { categoryId = TestData.CardsCategoryId, title = "Kartu", format = "AcceptOffers", publish = false });

        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await created.ReadJsonAsync()).GetProperty("listingId").GetString()!;
        var stored = await factory.Listings.FindAsync(id, CancellationToken.None);
        stored!.Status.ShouldBe(ListingStatus.Draft);
        stored.Price.ShouldBe(Money.Zero);
        stored.Photos.ShouldBeEmpty();

        using var edited = await client.PutAsJsonAsync(
            $"/api/listings/{id}",
            new { categoryId = TestData.CardsCategoryId, title = "Kartu 2", format = "FixedPrice", publish = false });

        edited.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.Listings.FindAsync(id, CancellationToken.None))!.Title.ShouldBe("Kartu 2");
    }

    [Fact]
    public async Task A_draft_without_a_title_is_still_refused()
    {
        using var client = factory.CreateClientAs("01JLISTUSER000000000EDRA2", "seller.notitle");

        using var response = await client.PostAsJsonAsync(
            "/api/listings", new { categoryId = TestData.CardsCategoryId, title = "  ", format = "FixedPrice", publish = false });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Kartu", 2_500_000, "PSA 10", "Bandung", "listing.title_too_short")]
    [InlineData(Title, 0, "PSA 10", "Bandung", "listing.price_too_low")]
    [InlineData(Title, 2_500_000, "", "Bandung", "listing.condition_required")]
    [InlineData(Title, 2_500_000, "PSA 10", "", "listing.city_required")]
    public async Task Publishing_still_demands_everything_a_draft_may_leave_out(
        string title, long price, string condition, string city, string code)
    {
        using var client = factory.CreateClientAs("01JLISTUSER000000000EDRA3", "seller.publishrules");

        using var response = await client.PostAsJsonAsync("/api/listings", new
        {
            categoryId = TestData.CardsCategoryId,
            title,
            priceRupiah = price,
            format = "FixedPrice",
            condition,
            city,
            photos = new[] { new { storageKey = TestData.PhotoKey("01JLISTUSER000000000EDRA3", "p"), width = 100, height = 100 } },
            publish = true,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Publishing_a_saved_incomplete_draft_from_the_dashboard_is_refused_with_the_first_missing_rule()
    {
        using var client = factory.CreateClientAs("01JLISTUSER000000000EDRA4", "seller.incomplete");
        using var created = await client.PostAsJsonAsync(
            "/api/listings", new { categoryId = TestData.CardsCategoryId, title = "Kartu", format = "FixedPrice", publish = false });
        var id = (await created.ReadJsonAsync()).GetProperty("listingId").GetString()!;

        using var response = await client.PostAsync($"/api/listings/{id}/publish", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.title_too_short");
        factory.SearchIndex.Indexed.ShouldNotContain(id);
    }

    [Fact]
    public async Task A_draft_that_was_archived_before_ever_being_live_is_edited_with_the_draft_form()
    {
        using var client = factory.CreateClientAs("01JLISTUSER000000000EDRA5", "seller.archiveddraft");
        using var created = await client.PostAsJsonAsync(
            "/api/listings", new { categoryId = TestData.CardsCategoryId, title = "Kartu", format = "FixedPrice", publish = false });
        var id = (await created.ReadJsonAsync()).GetProperty("listingId").GetString()!;
        (await client.PostAsync($"/api/listings/{id}/archive", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var edited = await client.PutAsJsonAsync(
            $"/api/listings/{id}",
            new { categoryId = TestData.CardsCategoryId, title = "A proper full title now", format = "FixedPrice", publish = false });

        edited.StatusCode.ShouldBe(HttpStatusCode.OK);
        var form = await (await client.GetAsync($"/api/listings/{id}")).ReadJsonAsync();
        form.GetProperty("wasPublished").GetBoolean().ShouldBeFalse();
        form.GetProperty("title").GetString().ShouldBe("A proper full title now");
    }

    // ─── Paging the seller's own listings ────────────────────────────────────────

    [Fact]
    public async Task The_sellers_listings_page_with_a_cursor_past_fifty()
    {
        var sellerId = "01JLISTUSER00000000PAGING1";
        for (var i = 0; i < 52; i++)
        {
            factory.Listings.Seed(Listing.CreateDraft(
                sellerId, TestData.CardsCategoryId, $"Paging listing number {i:D2}", Money.FromRupiah(100_000),
                ListingFormat.FixedPrice, "Mint", "Bandung", factory.Clock));
        }

        using var client = factory.CreateClientAs(sellerId, "seller.paging");

        var first = await (await client.GetAsync("/api/listings/mine")).ReadJsonAsync();
        first.GetProperty("items").GetArrayLength().ShouldBe(50);
        var cursor = first.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        var second = await (await client.GetAsync($"/api/listings/mine?cursor={Uri.EscapeDataString(cursor!)}")).ReadJsonAsync();
        second.GetProperty("items").GetArrayLength().ShouldBe(2);

        // The last page carries no cursor (the serializer omits nulls).
        second.TryGetProperty("nextCursor", out var lastCursor).ShouldBeFalse(
            "the last page must not offer another page");
        lastCursor.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Undefined);
    }
}
