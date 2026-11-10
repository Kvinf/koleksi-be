using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>Dicari end to end: posting, proposing, accepting (including an upsell), and paying through the normal checkout.</summary>
public sealed class BountyEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task Posting_needs_a_signed_in_user()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsJsonAsync("/api/bounties", PostBody());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Posting_without_a_delivery_address_is_refused()
    {
        var buyer = NewUserId();
        using var client = factory.CreateClientAs(buyer, "tanpa.alamat");

        using var response = await client.PostAsJsonAsync("/api/bounties", PostBody());

        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("bounty.address_required");
    }

    [Fact]
    public async Task A_posted_dicari_has_its_own_id_and_a_readable_code_and_takes_the_city_from_the_buyers_address()
    {
        var buyer = await SeedBuyerAsync();
        using var client = factory.CreateClientAs(buyer, "pembeli.satu");

        using var response = await client.PostAsJsonAsync("/api/bounties", PostBody());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        var bountyId = body.GetProperty("bountyId").GetString()!;
        body.GetProperty("code").GetString().ShouldBe(BountyCode.For(bountyId));
        body.GetProperty("code").GetString()!.ShouldStartWith("DCR-");

        using var detail = await client.GetAsync($"/api/bounties/{bountyId}");
        var card = (await detail.ReadJsonAsync()).GetProperty("card");
        card.GetProperty("shipToCity").GetString().ShouldBe("Bandung");
        card.GetProperty("maxBudgetRupiah").GetInt64().ShouldBe(2_000_000);
    }

    [Fact]
    public async Task At_most_five_dicari_can_be_open_at_once()
    {
        var buyer = await SeedBuyerAsync();
        using var client = factory.CreateClientAs(buyer, "pembeli.banyak");

        for (var i = 0; i < Bounty.MaxOpenPerBuyer; i++)
        {
            using var ok = await client.PostAsJsonAsync("/api/bounties", PostBody($"Dicari kartu langka nomor {i}"));
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var sixth = await client.PostAsJsonAsync("/api/bounties", PostBody("Dicari kartu langka yang keenam"));

        (await sixth.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("bounty.limit_reached");
    }

    [Fact]
    public async Task A_repeated_post_with_the_same_key_creates_one_dicari()
    {
        var buyer = await SeedBuyerAsync();
        using var client = factory.CreateClientAs(buyer, "pembeli.dua");
        var key = Guid.NewGuid().ToString();

        using var first = await PostWithKeyAsync(client, "/api/bounties", key, PostBody());
        using var second = await PostWithKeyAsync(client, "/api/bounties", key, PostBody());

        var firstId = (await first.ReadJsonAsync()).GetProperty("bountyId").GetString();
        var secondId = (await second.ReadJsonAsync()).GetProperty("bountyId").GetString();
        secondId.ShouldBe(firstId);

        using var mine = await client.GetAsync("/api/bounties/mine");
        (await mine.ReadJsonAsync()).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task The_public_board_shows_a_dicari_without_the_buyers_id_or_any_proposal_content()
    {
        var (buyer, bountyId) = await PostAsync("Dicari Charizard untuk papan publik");
        var listing = SeedSellerListing(Money.FromRupiah(1_800_000));
        await ProposeAsync(bountyId, listing, 1_800_000, "Kondisi mulus");
        using var anonymous = factory.CreateClient();

        using var board = await anonymous.GetAsync("/api/bounties?category=kartu");

        board.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await board.Content.ReadAsStringAsync();
        text.ShouldContain(bountyId);
        text.ShouldNotContain(buyer);
        text.ShouldNotContain("Kondisi mulus");
        text.ShouldNotContain("buyerId", Case.Insensitive);
    }

    [Fact]
    public async Task The_owner_sees_every_proposal_a_seller_sees_only_their_own_and_the_public_sees_a_count()
    {
        var (buyer, bountyId) = await PostAsync("Dicari tiga proposal berbeda");
        var sellerListing = SeedSellerListing(Money.FromRupiah(1_900_000));
        var rivalListing = SeedRivalListing(Money.FromRupiah(1_950_000));
        await ProposeAsync(bountyId, sellerListing, 1_900_000, null);
        await ProposeAsync(bountyId, rivalListing, 1_950_000, null, TestData.RivalId, TestData.RivalUsername);

        using var owner = factory.CreateClientAs(buyer, "pembeli.pemilik");
        var ownerView = await (await owner.GetAsync($"/api/bounties/{bountyId}")).ReadJsonAsync();
        ownerView.GetProperty("role").GetString().ShouldBe("owner");
        ownerView.GetProperty("proposals").GetArrayLength().ShouldBe(2);

        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        var sellerView = await (await seller.GetAsync($"/api/bounties/{bountyId}")).ReadJsonAsync();
        sellerView.GetProperty("role").GetString().ShouldBe("seller");
        sellerView.GetProperty("proposals").GetArrayLength().ShouldBe(1);
        sellerView.GetProperty("proposals")[0].GetProperty("listingId").GetString().ShouldBe(sellerListing.Id);

        using var anonymous = factory.CreateClient();
        var publicView = await (await anonymous.GetAsync($"/api/bounties/{bountyId}")).ReadJsonAsync();
        publicView.GetProperty("role").GetString().ShouldBe("public");
        publicView.GetProperty("proposals").GetArrayLength().ShouldBe(0);
        publicView.GetProperty("card").GetProperty("proposalCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_seller_cannot_propose_on_their_own_dicari_or_twice()
    {
        var (_, bountyId) = await PostAsync("Dicari yang tidak bisa dijawab dua kali", buyer: TestData.SellerId);
        var listing = SeedSellerListing(Money.FromRupiah(1_900_000));
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var own = await seller.PostAsJsonAsync(
            $"/api/bounties/{bountyId}/proposals", new { listingId = listing.Id, priceRupiah = 1_900_000 });
        (await own.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("bounty.self_proposal");

        var (_, other) = await PostAsync("Dicari lain untuk proposal ganda");
        (await ProposeAsync(other, listing, 1_900_000, null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var again = await ProposeAsync(other, listing, 1_800_000, null);
        (await again.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("bounty.already_proposed");
    }

    [Fact]
    public async Task Accepting_a_proposal_within_budget_makes_an_accepted_offer_with_its_own_id_and_checks_out_at_that_price()
    {
        var (buyer, bountyId) = await PostAsync("Dicari dalam anggaran", budget: 2_000_000);
        var listing = SeedSellerListing(Money.FromRupiah(2_500_000));
        var proposalId = await ProposeAndGetIdAsync(bountyId, listing, 1_800_000);
        using var client = factory.CreateClientAs(buyer, "pembeli.terima");

        using var accept = await client.PostAsync($"/api/bounties/{bountyId}/proposals/{proposalId}/accept", null);

        accept.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await accept.ReadJsonAsync();
        var offerId = accepted.GetProperty("offerId").GetString()!;
        offerId.ShouldNotBe(bountyId);
        offerId.ShouldNotBe(proposalId);
        accepted.GetProperty("isUpsell").GetBoolean().ShouldBeFalse();
        accepted.GetProperty("priceRupiah").GetInt64().ShouldBe(1_800_000);

        using var checkout = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(offerId));
        checkout.StatusCode.ShouldBe(HttpStatusCode.OK);
        var order = await checkout.ReadJsonAsync();
        order.GetProperty("grandTotalRupiah").GetInt64().ShouldBe(1_800_000 + 15_000);
        var orderId = order.GetProperty("orderId").GetString()!;
        orderId.ShouldNotBe(offerId);

        using var detail = await client.GetAsync($"/api/orders/{orderId}");
        var row = (await detail.ReadJsonAsync()).GetProperty("order");
        row.GetProperty("source").GetString().ShouldBe("BountyOffer");
        row.GetProperty("bountyId").GetString().ShouldBe(bountyId);
        row.GetProperty("bountyCode").GetString().ShouldBe(BountyCode.For(bountyId));
    }

    [Fact]
    public async Task An_upsell_over_budget_is_flagged_and_once_accepted_is_a_checkout_item_at_the_proposed_price()
    {
        var (buyer, bountyId) = await PostAsync("Dicari dengan tawaran di atas anggaran", budget: 1_000_000);
        var listing = SeedSellerListing(Money.FromRupiah(3_000_000));
        var proposalId = await ProposeAndGetIdAsync(bountyId, listing, 1_500_000);
        using var client = factory.CreateClientAs(buyer, "pembeli.upsell");

        var before = await (await client.GetAsync($"/api/bounties/{bountyId}")).ReadJsonAsync();
        var shown = before.GetProperty("proposals")[0];
        shown.GetProperty("isUpsell").GetBoolean().ShouldBeTrue();
        shown.GetProperty("amountOverBudgetRupiah").GetInt64().ShouldBe(500_000);

        using var accept = await client.PostAsync($"/api/bounties/{bountyId}/proposals/{proposalId}/accept", null);
        accept.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await accept.ReadJsonAsync();
        accepted.GetProperty("isUpsell").GetBoolean().ShouldBeTrue();
        accepted.GetProperty("amountOverBudgetRupiah").GetInt64().ShouldBe(500_000);

        using var checkout = await client.PostAsJsonAsync(
            "/api/orders/checkout", CheckoutBody(accepted.GetProperty("offerId").GetString()!));
        checkout.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await checkout.ReadJsonAsync()).GetProperty("grandTotalRupiah").GetInt64().ShouldBe(1_500_000 + 15_000);

        // The buyer's stated budget is theirs and is never rewritten by a seller's price.
        var after = await (await client.GetAsync($"/api/bounties/{bountyId}")).ReadJsonAsync();
        after.GetProperty("card").GetProperty("maxBudgetRupiah").GetInt64().ShouldBe(1_000_000);
    }

    [Fact]
    public async Task Only_the_owner_can_accept_and_a_stranger_gets_the_not_found_a_missing_id_gets()
    {
        var (_, bountyId) = await PostAsync("Dicari yang tidak bisa diterima orang lain");
        var listing = SeedSellerListing(Money.FromRupiah(1_900_000));
        var proposalId = await ProposeAndGetIdAsync(bountyId, listing, 1_900_000);
        using var rival = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var response = await rival.PostAsync($"/api/bounties/{bountyId}/proposals/{proposalId}/accept", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("bounty.not_found");
    }

    [Fact]
    public async Task A_second_accept_on_a_dicari_with_a_deal_in_flight_is_refused()
    {
        var (buyer, bountyId) = await PostAsync("Dicari dengan dua proposal bersaing", budget: 2_000_000);
        var firstListing = SeedSellerListing(Money.FromRupiah(1_900_000));
        var secondListing = SeedRivalListing(Money.FromRupiah(1_950_000));
        var first = await ProposeAndGetIdAsync(bountyId, firstListing, 1_900_000);
        var second = await ProposeAndGetIdAsync(bountyId, secondListing, 1_950_000, TestData.RivalId, TestData.RivalUsername);
        using var client = factory.CreateClientAs(buyer, "pembeli.dua.proposal");

        (await client.PostAsync($"/api/bounties/{bountyId}/proposals/{first}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var refused = await client.PostAsync($"/api/bounties/{bountyId}/proposals/{second}/accept", null);

        (await refused.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("bounty.deal_in_progress");
    }

    [Fact]
    public async Task A_normal_accepted_offer_still_checks_out_as_an_ordinary_offer()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000), format: ListingFormat.AcceptOffers);
        factory.Listings.Seed(listing);
        var offer = Offer.Create(
            listing.Id, TestData.BidderId, listing.SellerId, Money.FromRupiah(2_000_000), listing.Price, null, null, factory.Clock);
        offer.Accept(listing.SellerId, factory.Clock);
        factory.Offers.Seed(offer);
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var checkout = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(offer.Id));
        var orderId = (await checkout.ReadJsonAsync()).GetProperty("orderId").GetString();
        var detail = (await (await client.GetAsync($"/api/orders/{orderId}")).ReadJsonAsync()).GetProperty("order");

        detail.GetProperty("source").GetString().ShouldBe("AcceptedOffer");
        (detail.TryGetProperty("bountyCode", out var code) && code.ValueKind != JsonValueKind.Null).ShouldBeFalse();
    }

    [Fact]
    public async Task Closing_a_dicari_lapses_its_pending_proposals_and_takes_it_off_the_board()
    {
        var (buyer, bountyId) = await PostAsync("Dicari yang segera ditutup");
        var listing = SeedSellerListing(Money.FromRupiah(1_900_000));
        await ProposeAndGetIdAsync(bountyId, listing, 1_900_000);
        using var client = factory.CreateClientAs(buyer, "pembeli.tutup");

        using var close = await client.PostAsync($"/api/bounties/{bountyId}/close", null);
        close.StatusCode.ShouldBe(HttpStatusCode.OK);

        var view = await (await client.GetAsync($"/api/bounties/{bountyId}")).ReadJsonAsync();
        view.GetProperty("card").GetProperty("status").GetString().ShouldBe("Closed");
        view.GetProperty("proposals")[0].GetProperty("status").GetString().ShouldBe("Lapsed");

        using var anonymous = factory.CreateClient();
        (await (await anonymous.GetAsync("/api/bounties?category=kartu")).Content.ReadAsStringAsync()).ShouldNotContain(bountyId);
    }

    [Fact]
    public async Task A_malformed_id_is_a_404_before_it_reaches_the_table()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.GetAsync($"/api/bounties/{new string('x', 3000)}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ helpers

    private static string NewUserId() => Guid.NewGuid().ToString();

    private async Task<string> SeedBuyerAsync(string? userId = null)
    {
        userId ??= NewUserId();
        var profile = UserProfile.CreateEmpty(userId, factory.Clock);
        profile.UpdateDetails(
            "Pembeli Uji",
            [new Address("alamat-1", "Rumah", "Pembeli Uji", "081234567890", "Jl. Merdeka 1", "Bandung", "Jawa Barat", "40111", true)],
            factory.Clock);
        await factory.Profiles.AddAsync(profile, CancellationToken.None);
        return userId;
    }

    private async Task<(string Buyer, string BountyId)> PostAsync(string title, long budget = 2_000_000, string? buyer = null)
    {
        buyer = await SeedBuyerAsync(buyer);
        using var client = factory.CreateClientAs(buyer, "pembeli." + buyer[..6]);
        using var response = await client.PostAsJsonAsync("/api/bounties", PostBody(title, budget));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (buyer, (await response.ReadJsonAsync()).GetProperty("bountyId").GetString()!);
    }

    private Listing SeedRivalListing(Money price)
    {
        var listing = TestData.ActiveListingBy(TestData.RivalId, "Kartu Pokemon Charizard Holo Langka", price);
        factory.Listings.Seed(listing);
        return listing;
    }

    private Listing SeedSellerListing(Money price)
    {
        var listing = TestData.ActiveListing(price: price);
        factory.Listings.Seed(listing);
        return listing;
    }

    private async Task<HttpResponseMessage> ProposeAsync(
        string bountyId, Listing listing, long price, string? note, string? sellerId = null, string? sellerName = null)
    {
        using var client = factory.CreateClientAs(sellerId ?? TestData.SellerId, sellerName ?? TestData.SellerUsername);
        return await client.PostAsJsonAsync(
            $"/api/bounties/{bountyId}/proposals", new { listingId = listing.Id, priceRupiah = price, note });
    }

    private async Task<string> ProposeAndGetIdAsync(
        string bountyId, Listing listing, long price, string? sellerId = null, string? sellerName = null)
    {
        using var response = await ProposeAsync(bountyId, listing, price, null, sellerId, sellerName);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.ReadJsonAsync()).GetProperty("proposalId").GetString()!;
    }

    private static Task<HttpResponseMessage> PostWithKeyAsync(HttpClient client, string url, string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static object PostBody(string title = "Dicari PSA 10 Charizard Base Set 1999", long budget = 2_000_000) => new
    {
        title,
        categoryId = TestData.CardsCategoryId,
        facets = new Dictionary<string, string>(),
        description = "Mencari kartu dalam kondisi mulus.",
        conditions = new[] { "PSA 10" },
        maxBudgetRupiah = budget,
        photoKeys = Array.Empty<string>(),
        durationDays = 14,
    };

    private static object CheckoutBody(string offerId) => new
    {
        source = "AcceptedOffer",
        referenceId = offerId,
        shipTo = new
        {
            recipientName = "Pembeli Uji",
            phone = "081234567890",
            street = "Jl. Merdeka No. 1",
            city = "Bandung",
            province = "Jawa Barat",
            postalCode = "40111",
        },
        courierCode = "jne",
    };
}
