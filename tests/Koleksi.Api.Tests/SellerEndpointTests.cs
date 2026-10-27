using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Messaging;
using Koleksi.Domain.Users;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The public storefront, the signed-in user's own public identity, and the rule that no public or
/// counter-party-facing response carries the sign-in name (an email address for most accounts).
/// </summary>
public sealed class SellerEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string SellerEmail = "kevin.rahasia@mail.test";
    private const string BuyerEmail = "pembeli.rahasia@mail.test";

    private readonly string _sellerId = Guid.NewGuid().ToString();
    private readonly string _buyerId = Guid.NewGuid().ToString();

    [Theory]
    [InlineData("tidak-ada-toko")]
    [InlineData("kevin@gmail.com")]
    [InlineData("ab")]
    [InlineData("admin")]
    public async Task A_handle_nobody_owns_or_that_is_malformed_is_not_found(string handle)
    {
        using var visitor = factory.CreateClient();

        foreach (var path in new[] { "", "/listings", "/reviews" })
        {
            using var response = await visitor.GetAsync($"/api/sellers/{Uri.EscapeDataString(handle)}{path}");
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task The_storefront_is_public_and_reports_only_real_facts()
    {
        var handle = factory.PublicHandleOf(_sellerId);
        factory.Listings.Seed(ActiveListingBy(_sellerId, "PSA 10 Pikachu"));
        factory.Listings.Seed(ActiveListingBy(_sellerId, "Gunpla RG Nu"));
        var soldListing = ActiveListingBy(_sellerId, "Sudah laku");
        soldListing.MarkSold(new MutableClock());
        factory.Listings.Seed(soldListing);

        using var visitor = factory.CreateClient();
        using var response = await visitor.GetAsync($"/api/sellers/{handle}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.Public.ShouldBeTrue();

        var body = await response.ReadJsonAsync();
        body.GetProperty("handle").GetString().ShouldBe(handle);
        body.GetProperty("activeListingCount").GetInt32().ShouldBe(2);
        body.GetProperty("soldCount").GetInt32().ShouldBe(1);
        body.GetProperty("contactVerified").GetBoolean().ShouldBeFalse();
        body.GetProperty("rating").GetProperty("count").GetInt32().ShouldBe(0);
        body.GetProperty("rating").TryGetProperty("average", out _).ShouldBeFalse();
        body.GetProperty("memberSince").GetDateTimeOffset().ShouldNotBe(default);

        // Nothing identity-verified is ever claimed; nothing here is an account id.
        var text = await response.ReadTextAsync();
        text.ShouldNotContain(_sellerId);
        text.ShouldNotContain("idVerified", Case.Insensitive);
    }

    [Fact]
    public async Task A_seller_who_completed_the_code_check_shows_contact_verified()
    {
        var handle = factory.PublicHandleOf(_sellerId);
        var profile = UserProfile.CreateEmpty(_sellerId, new MutableClock());
        profile.MarkPhoneVerified("081234567890", new MutableClock());
        await factory.Profiles.AddAsync(profile, CancellationToken.None);

        using var visitor = factory.CreateClient();
        var body = await (await visitor.GetAsync($"/api/sellers/{handle}")).ReadJsonAsync();

        body.GetProperty("contactVerified").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Completing_the_email_code_fallback_does_not_show_contact_verified()
    {
        // PhoneVerified is set by the email fallback too (no number is recorded), and that proves an
        // inbox, not a phone: the storefront badge must not claim it (QA D-10).
        var handle = factory.PublicHandleOf(_sellerId);
        var profile = UserProfile.CreateEmpty(_sellerId, new MutableClock());
        profile.MarkPhoneVerified(null, new MutableClock());
        profile.PhoneVerified.ShouldBeTrue();
        await factory.Profiles.AddAsync(profile, CancellationToken.None);

        using var visitor = factory.CreateClient();
        var body = await (await visitor.GetAsync($"/api/sellers/{handle}")).ReadJsonAsync();

        body.GetProperty("contactVerified").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task The_items_tab_holds_fixed_price_listings_and_the_auctions_tab_only_live_auctions()
    {
        var handle = factory.PublicHandleOf(_sellerId);
        var fixedPrice = ActiveListingBy(_sellerId, "Kartu harga tetap");
        var auctionListing = ActiveListingBy(_sellerId, "Kartu lelang", ListingFormat.Auction);
        var auctionWithoutAuction = ActiveListingBy(_sellerId, "Lelang tanpa lelang", ListingFormat.Auction);
        factory.Listings.Seed(fixedPrice);
        factory.Listings.Seed(auctionListing);
        factory.Listings.Seed(auctionWithoutAuction);
        var auction = TestData.LiveAuction(auctionListing);
        await factory.Auctions.AddAsync(auction, CancellationToken.None);

        using var visitor = factory.CreateClient();

        var items = await (await visitor.GetAsync($"/api/sellers/{handle}/listings")).ReadJsonAsync();
        items.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString())
            .ShouldBe([fixedPrice.Id]);

        var auctionsResponse = await visitor.GetAsync($"/api/sellers/{handle}/listings?kind=auctions");
        var auctions = await auctionsResponse.ReadJsonAsync();
        var card = auctions.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        card.GetProperty("id").GetString().ShouldBe(auctionListing.Id);
        card.GetProperty("auctionEndsAt").GetDateTimeOffset().ShouldBe(auction.EndsAt);
        card.GetProperty("sellerUsername").GetString().ShouldBe(handle);

        var text = await auctionsResponse.ReadTextAsync();
        text.ShouldNotContain(_sellerId);
    }

    [Fact]
    public async Task Browse_cards_show_the_handle_even_for_rows_indexed_with_the_email()
    {
        var listing = ActiveListingBy(_sellerId, "Kartu lama");
        factory.Listings.Seed(listing);

        // A row written before the projection carried a seller id: it holds only the sign-in name.
        factory.SearchIndex.Seed(new ListingSummary(
            listing.Id, listing.Title, listing.CategoryId, TestData.CardsCategorySlug,
            listing.Price.Rupiah, listing.Format, ListingStatus.Active, listing.Condition,
            SellerEmail, listing.City, PrimaryPhotoKey: null, AuctionEndsAt: null, BidCount: 0));

        using var visitor = factory.CreateClient();
        using var response = await visitor.GetAsync("/api/catalog/browse?kategori=kartu");

        var text = await response.ReadTextAsync();
        text.ShouldNotContain(SellerEmail);
        text.ShouldNotContain(_sellerId);

        var card = (await response.ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == listing.Id);
        card.GetProperty("sellerUsername").GetString().ShouldBe(factory.PublicHandleOf(_sellerId));
    }

    [Fact]
    public async Task The_item_page_names_the_seller_by_handle_and_a_bid_history_masks_the_handle_not_the_email()
    {
        var listing = ActiveListingBy(_sellerId, "Lelang rahasia", ListingFormat.Auction);
        factory.Listings.Seed(listing);
        await factory.Auctions.AddAsync(TestData.LiveAuction(listing), CancellationToken.None);

        using var bidder = factory.CreateClientAs(_buyerId, BuyerEmail);
        using var bid = await bidder.PostBidAsync(listing.Id, 1_500_000);
        bid.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var visitor = factory.CreateClient();
        using var response = await visitor.GetAsync($"/api/catalog/listings/{Slug.From(listing.Title)}-{listing.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.ReadTextAsync();
        text.ShouldNotContain(BuyerEmail);
        text.ShouldNotContain("pembeli.rahasia");
        text.ShouldNotContain(_sellerId);
        text.ShouldNotContain(_buyerId);

        var body = await response.ReadJsonAsync();
        body.GetProperty("seller").GetProperty("handle").GetString().ShouldBe(factory.PublicHandleOf(_sellerId));
        body.GetProperty("auction").GetProperty("history").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("maskedBidder").GetString()
            .ShouldBe(BidderAlias_Mask(factory.PublicHandleOf(_buyerId)));
    }

    [Fact]
    public async Task A_conversation_names_the_other_side_by_handle_for_new_and_legacy_rows()
    {
        var listing = ActiveListingBy(_sellerId, "Barang obrolan");
        factory.Listings.Seed(listing);

        using var buyer = factory.CreateClientAs(_buyerId, BuyerEmail);
        using var started = await buyer.PostAsJsonAsync("/api/conversations", new { listingId = listing.Id, body = "Halo" });
        started.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A thread begun before this change: both parties stored by their sign-in email.
        var legacyListing = ActiveListingBy(_sellerId, "Barang obrolan lama");
        factory.Listings.Seed(legacyListing);
        var legacy = Conversation.Start(
            legacyListing.Id, legacyListing.Title, null, _buyerId, BuyerEmail, _sellerId, SellerEmail, new MutableClock());
        var message = ChatMessage.Create(legacy.Id, _buyerId, "Pesan lama", new MutableClock());
        legacy.RecordMessage(message);
        await factory.Conversations.AppendAsync(legacy, message, CancellationToken.None);

        using var seller = factory.CreateClientAs(_sellerId, SellerEmail);
        foreach (var (client, other) in new[] { (buyer, _sellerId), (seller, _buyerId) })
        {
            using var inbox = await client.GetAsync("/api/conversations");
            var text = await inbox.ReadTextAsync();
            text.ShouldNotContain(BuyerEmail);
            text.ShouldNotContain(SellerEmail);

            foreach (var row in (await inbox.ReadJsonAsync()).GetProperty("items").EnumerateArray())
            {
                row.GetProperty("otherPartyUsername").GetString().ShouldBe(factory.PublicHandleOf(other));
            }
        }
    }

    [Fact]
    public async Task The_owner_can_choose_a_handle_and_a_display_name_and_the_storefront_follows()
    {
        var firstHandle = factory.PublicHandleOf(_sellerId);
        using var seller = factory.CreateClientAs(_sellerId, SellerEmail);
        var newHandle = "toko-" + Guid.NewGuid().ToString("N")[..8];

        using var save = await seller.PutAsJsonAsync(
            "/api/profile/public", new { handle = newHandle.ToUpperInvariant(), displayName = "  Toko   Kevin " });

        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = await save.ReadJsonAsync();
        saved.GetProperty("handle").GetString().ShouldBe(newHandle);
        saved.GetProperty("displayName").GetString().ShouldBe("Toko Kevin");

        using var visitor = factory.CreateClient();
        (await visitor.GetAsync($"/api/sellers/{newHandle}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await visitor.GetAsync($"/api/sellers/{firstHandle}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var shop = await (await visitor.GetAsync($"/api/sellers/{newHandle}")).ReadJsonAsync();
        shop.GetProperty("displayName").GetString().ShouldBe("Toko Kevin");

        using var own = await seller.GetAsync("/api/profile/public");
        (await own.ReadJsonAsync()).GetProperty("handle").GetString().ShouldBe(newHandle);
    }

    [Fact]
    public async Task A_handle_another_account_owns_is_refused_and_the_old_one_is_kept()
    {
        var taken = factory.PublicHandleOf(Guid.NewGuid().ToString());
        var mine = factory.PublicHandleOf(_sellerId);
        using var seller = factory.CreateClientAs(_sellerId, SellerEmail);

        using var response = await seller.PutAsJsonAsync("/api/profile/public", new { handle = taken });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("handle.taken");
        factory.PublicHandleOf(_sellerId).ShouldBe(mine);
    }

    [Theory]
    [InlineData("kevin@gmail.com", null, "handle.invalid_format")]
    [InlineData("ab", null, "handle.invalid_format")]
    [InlineData("admin", null, "handle.reserved")]
    [InlineData("koleksi-resmi", null, "handle.reserved")]
    [InlineData("toko-bagus", "kevin@gmail.com", "profile.display_name_invalid")]
    public async Task An_unacceptable_handle_or_display_name_is_a_bad_request(string handle, string? displayName, string code)
    {
        using var seller = factory.CreateClientAs(_sellerId, SellerEmail);

        using var response = await seller.PutAsJsonAsync("/api/profile/public", new { handle, displayName });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task A_second_handle_change_inside_the_cooldown_is_refused()
    {
        using var seller = factory.CreateClientAs(_sellerId, SellerEmail);
        var first = "toko-" + Guid.NewGuid().ToString("N")[..8];
        var second = "toko-" + Guid.NewGuid().ToString("N")[..8];

        (await seller.PutAsJsonAsync("/api/profile/public", new { handle = first })).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var again = await seller.PutAsJsonAsync("/api/profile/public", new { handle = second });

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("handle.change_too_soon");
    }

    [Fact]
    public async Task The_own_profile_requires_a_signed_in_caller()
    {
        using var visitor = factory.CreateClient();

        (await visitor.GetAsync("/api/profile/public")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await visitor.PutAsJsonAsync("/api/profile/public", new { handle = "toko-bagus" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string BidderAlias_Mask(string handle) => Koleksi.Domain.Auctions.BidderAlias.Mask(handle);

    private static Listing ActiveListingBy(string sellerId, string title, ListingFormat format = ListingFormat.FixedPrice)
    {
        var clock = new MutableClock();
        var listing = Listing.CreateDraft(
            sellerId, TestData.CardsCategoryId, title, Money.FromRupiah(2_500_000), format, "PSA 10", "Bandung", clock);
        listing.SetPhotos([ListingPhoto.Create("listing/photo-one.jpg", 1200, 1200)], clock);
        listing.Publish(clock);
        return listing;
    }
}
