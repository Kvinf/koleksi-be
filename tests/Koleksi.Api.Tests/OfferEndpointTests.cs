using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>Making, answering, and listing offers, end to end.</summary>
public sealed class OfferEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task A_buyer_can_make_an_offer_below_asking()
    {
        var listing = SeedNegoListing();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostAsJsonAsync(
            $"/api/listings/{listing.Id}/offers", new { amountRupiah = 2_000_000, message = "Bisa kurang?" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("status").GetString().ShouldBe("Pending");
    }

    [Fact]
    public async Task A_second_open_offer_on_the_same_listing_is_refused()
    {
        var listing = SeedNegoListing();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var first = await client.PostAsJsonAsync(
            $"/api/listings/{listing.Id}/offers", new { amountRupiah = 2_000_000 });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var second = await client.PostAsJsonAsync(
            $"/api/listings/{listing.Id}/offers", new { amountRupiah = 2_100_000 });

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.already_active");
    }

    [Fact]
    public async Task The_seller_can_accept_a_pending_offer()
    {
        var (listing, offer) = SeedNegoListingWithOffer();
        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.PostAsync($"/api/offers/{offer.Id}/accept", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("Accepted");
        _ = listing;
    }

    [Fact]
    public async Task A_stranger_cannot_accept_someone_elses_offer()
    {
        var (_, offer) = SeedNegoListingWithOffer();
        using var client = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var response = await client.PostAsync($"/api/offers/{offer.Id}/accept", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.forbidden");
    }

    [Fact]
    public async Task The_seller_can_decline_a_pending_offer()
    {
        var (_, offer) = SeedNegoListingWithOffer();
        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.PostAsync($"/api/offers/{offer.Id}/decline", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("Declined");
    }

    [Fact]
    public async Task The_buyer_can_withdraw_their_own_pending_offer()
    {
        var (_, offer) = SeedNegoListingWithOffer();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostAsync($"/api/offers/{offer.Id}/withdraw", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("Withdrawn");
    }

    [Fact]
    public async Task Received_offers_list_for_the_seller_and_sent_offers_list_for_the_buyer()
    {
        var (listing, offer) = SeedNegoListingWithOffer();
        _ = listing;

        using var sellerClient = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        using var received = await sellerClient.GetAsync("/api/offers/received");
        received.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await received.ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .ShouldContain(item => item.GetProperty("id").GetString() == offer.Id);

        using var buyerClient = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var sent = await buyerClient.GetAsync("/api/offers/mine");
        sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sent.ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .ShouldContain(item => item.GetProperty("id").GetString() == offer.Id);
    }

    private Listing SeedNegoListing()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000), format: ListingFormat.AcceptOffers);
        factory.Listings.Seed(listing);
        return listing;
    }

    private (Listing Listing, Offer Offer) SeedNegoListingWithOffer()
    {
        var listing = SeedNegoListing();
        var offer = Offer.Create(
            listing.Id, TestData.BidderId, listing.SellerId, Money.FromRupiah(2_000_000), listing.Price,
            "Bisa kurang?", autoDeclineBelowPercent: null, new FixedTestClock());
        factory.Offers.Seed(offer);
        return (listing, offer);
    }

    private sealed class FixedTestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = MutableClock.Origin;
    }
}
