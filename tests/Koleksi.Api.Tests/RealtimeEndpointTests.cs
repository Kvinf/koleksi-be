using System.Net;
using System.Net.Http.Json;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>What the API pushes to open pages, and how a connection proves who it is.</summary>
public sealed class RealtimeEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    // A fresh user per test: the messaging and bidding buckets are per subject and shared by the fixture.
    private readonly string _buyerId = Guid.NewGuid().ToString();
    private readonly string _buyerName = "buyer." + Guid.NewGuid().ToString("N")[..8];

    // ─── Tickets ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Minting_a_ticket_needs_a_signed_in_caller()
    {
        using var anonymous = factory.CreateClientFrom("203.0.113.40");

        using var response = await anonymous.PostAsync("/api/realtime/tickets", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_ticket_belongs_to_the_caller_and_can_be_redeemed_once()
    {
        using var client = factory.CreateClientAs(_buyerId, _buyerName);

        using var response = await client.PostAsync("/api/realtime/tickets", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        var ticket = body.GetProperty("ticket").GetString()!;
        body.GetProperty("expiresInSeconds").GetInt32().ShouldBe(60);

        (await factory.RealtimeTickets.ConsumeAsync(ticket, CancellationToken.None)).ShouldBe(_buyerId);
        (await factory.RealtimeTickets.ConsumeAsync(ticket, CancellationToken.None)).ShouldBeNull();
    }

    // ─── Chat ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_buyers_message_pushes_a_signal_to_the_seller_and_nobody_else()
    {
        var listing = TestData.ActiveListing();
        factory.Listings.Seed(listing);
        factory.Listings.SellerUsernames[listing.Id] = TestData.SellerUsername;
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var started = await buyer.PostAsJsonAsync(
            "/api/conversations", new { listingId = listing.Id, body = "RAHASIA-ISI-PESAN" });
        var conversationId = (await started.ReadJsonAsync()).GetProperty("conversationId").GetString();

        var push = factory.Realtime.Published
            .Where(p => p.Topic == RealtimeTopics.User(TestData.SellerId))
            .ShouldHaveSingleItem();
        push.Type.ShouldBe("chat");
        push.Data.ToString()!.ShouldContain(conversationId!);

        // The signal says that something changed, never what: the text stays behind the
        // participant check on the ordinary read.
        push.Data.ToString()!.ShouldNotContain("RAHASIA-ISI-PESAN");
        factory.Realtime.Published.ShouldNotContain(p => p.Topic == RealtimeTopics.User(_buyerId));
    }

    [Fact]
    public async Task A_sellers_reply_pushes_a_signal_to_the_buyer()
    {
        var listing = TestData.ActiveListing();
        factory.Listings.Seed(listing);
        factory.Listings.SellerUsernames[listing.Id] = TestData.SellerUsername;
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);
        using var started = await buyer.PostAsJsonAsync("/api/conversations", new { listingId = listing.Id, body = "Halo" });
        var conversationId = (await started.ReadJsonAsync()).GetProperty("conversationId").GetString();

        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        using var reply = await seller.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages", new { body = "Ada kak" });

        reply.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Realtime.Published
            .Where(p => p.Topic == RealtimeTopics.User(_buyerId))
            .ShouldHaveSingleItem()
            .Type.ShouldBe("chat");
    }

    [Fact]
    public async Task A_message_is_still_saved_when_the_push_fails()
    {
        var listing = TestData.ActiveListing();
        factory.Listings.Seed(listing);
        factory.Listings.SellerUsernames[listing.Id] = TestData.SellerUsername;
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        factory.Realtime.FailWith = new InvalidOperationException("the socket API is down");
        try
        {
            using var response = await buyer.PostAsJsonAsync(
                "/api/conversations", new { listingId = listing.Id, body = "Halo" });

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Realtime.FailWith = null;
        }

        using var thread = await buyer.GetAsync($"/api/listings/{listing.Id}/conversation");
        (await thread.ReadJsonAsync()).GetProperty("messages").GetArrayLength().ShouldBe(1);
    }

    // ─── Auction ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_accepted_bid_is_pushed_on_the_listings_public_topic_without_the_leaders_identity()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(1_000_000), format: ListingFormat.Auction);
        factory.Listings.Seed(listing);
        factory.Auctions.Seed(TestData.LiveAuction(listing, Money.FromRupiah(1_000_000)));
        using var bidder = factory.CreateClientAs(_buyerId, _buyerName);

        using var response = await bidder.PostBidAsync(listing.Id, 4_000_000);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var push = factory.Realtime.Published
            .Where(p => p.Topic == RealtimeTopics.Auction(listing.Id))
            .ShouldHaveSingleItem();
        push.Type.ShouldBe(RealtimeAuctionEventPublisher.PriceEvent);

        var update = push.Data.ShouldBeOfType<AuctionPriceUpdate>();
        update.CurrentPriceRupiah.ShouldBe(4_000_000);
        update.MaskedBidder.ShouldNotContain(_buyerId);
    }

    // ─── Topics ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("auction:06GH00WHBDXJD3BHAA2F53YWP0", true)]
    [InlineData("auction:abc-DEF_123", true)]
    [InlineData("user:someone", false)]
    [InlineData("auction:", false)]
    [InlineData("auction:with space", false)]
    [InlineData("auction:../../etc", false)]
    [InlineData("AUCTION:abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_well_formed_auction_topics_can_be_subscribed_to(string? topic, bool expected) =>
        RealtimeTopics.IsSubscribable(topic).ShouldBe(expected);

    [Fact]
    public void A_topic_over_the_length_limit_cannot_be_subscribed_to() =>
        RealtimeTopics.IsSubscribable("auction:" + new string('a', 65)).ShouldBeFalse();

    [Fact]
    public void A_user_topic_never_reveals_the_user_id_to_the_browser()
    {
        RealtimeTopics.WireName(RealtimeTopics.User("sub-123")).ShouldBe("user");
        RealtimeTopics.WireName(RealtimeTopics.Auction("L1")).ShouldBe("auction:L1");
    }
}
