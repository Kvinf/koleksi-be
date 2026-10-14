using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The whole negotiation end to end: counter, answering a counter, withdrawing, expiry,
/// the push to the other party, and the once-only checkout of an accepted offer.
/// </summary>
/// <remarks>
/// The factory's clock is shared by every test in the class, so none of them advance it.
/// A lapsed offer is instead seeded as one created days ago.
/// </remarks>
public sealed class OfferNegotiationEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    // ─── counter ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_seller_counters_and_the_buyers_original_amount_is_kept()
    {
        var (_, offer) = SeedPending();
        using var seller = SellerClient();

        using var response = await seller.PostAsJsonAsync($"/api/offers/{offer.Id}/counter", new { counterAmountRupiah = 2_500_000 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("status").GetString().ShouldBe("Countered");
        body.GetProperty("counterAmountRupiah").GetInt64().ShouldBe(2_500_000);

        var stored = (await factory.Offers.FindAsync(offer.Id, CancellationToken.None))!;
        stored.Amount.Rupiah.ShouldBe(2_000_000);
        stored.CounterAmount!.Value.Rupiah.ShouldBe(2_500_000);
    }

    [Theory]
    [InlineData(1_900_000, "offer.counter_not_higher")]
    [InlineData(2_000_000, "offer.counter_not_higher")]
    [InlineData(3_500_000, "offer.counter_above_asking")]
    [InlineData(-5, "offer.invalid_amount")]
    public async Task A_counter_outside_the_allowed_range_is_refused(long amount, string code)
    {
        var (_, offer) = SeedPending();
        using var seller = SellerClient();

        using var response = await seller.PostAsJsonAsync($"/api/offers/{offer.Id}/counter", new { counterAmountRupiah = amount });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task The_buyer_cannot_counter_their_own_offer()
    {
        var (_, offer) = SeedPending();
        using var buyer = BuyerClient();

        using var response = await buyer.PostAsJsonAsync($"/api/offers/{offer.Id}/counter", new { counterAmountRupiah = 2_500_000 });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_second_counter_is_refused()
    {
        var (_, offer) = SeedCountered();
        using var seller = SellerClient();

        using var response = await seller.PostAsJsonAsync($"/api/offers/{offer.Id}/counter", new { counterAmountRupiah = 2_700_000 });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.not_your_turn.forbidden");
    }

    // ─── answering a counter ────────────────────────────────────────────────

    [Fact]
    public async Task The_buyer_accepts_a_counter_at_the_counter_amount()
    {
        var (_, offer) = SeedCountered();
        using var buyer = BuyerClient();

        using var response = await buyer.PostAsync($"/api/offers/{offer.Id}/accept", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("status").GetString().ShouldBe("Accepted");
        body.GetProperty("amountRupiah").GetInt64().ShouldBe(2_500_000);
    }

    [Fact]
    public async Task The_buyer_declines_a_counter()
    {
        var (_, offer) = SeedCountered();
        using var buyer = BuyerClient();

        using var response = await buyer.PostAsync($"/api/offers/{offer.Id}/decline", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("Declined");
    }

    [Fact]
    public async Task The_seller_cannot_accept_or_decline_a_row_they_already_countered()
    {
        var (_, offer) = SeedCountered();
        using var seller = SellerClient();

        using var accept = await seller.PostAsync($"/api/offers/{offer.Id}/accept", null);
        using var decline = await seller.PostAsync($"/api/offers/{offer.Id}/decline", null);

        accept.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        decline.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await factory.Offers.FindAsync(offer.Id, CancellationToken.None))!.Status.ShouldBe(OfferStatus.Countered);
    }

    [Fact]
    public async Task The_buyer_cannot_accept_their_own_pending_offer()
    {
        var (_, offer) = SeedPending();
        using var buyer = BuyerClient();

        using var response = await buyer.PostAsync($"/api/offers/{offer.Id}/accept", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.not_your_turn.forbidden");
    }

    [Fact]
    public async Task A_stranger_cannot_touch_a_countered_offer()
    {
        var (_, offer) = SeedCountered();
        using var stranger = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        // 404, exactly as for an id that does not exist (QA S-08), on every write route.
        var missing = EntityId.New(MutableClock.Origin);
        foreach (var action in new[] { "accept", "decline", "withdraw" })
        {
            using var real = await stranger.PostAsync($"/api/offers/{offer.Id}/{action}", null);
            using var absent = await stranger.PostAsync($"/api/offers/{missing}/{action}", null);

            real.StatusCode.ShouldBe(HttpStatusCode.NotFound, action);
            (await real.ReadTextAsync()).ShouldBe(await absent.ReadTextAsync(), action);
        }

        using var counter = await stranger.PostAsJsonAsync($"/api/offers/{offer.Id}/counter", new { counterAmountRupiah = 2_500_000 });
        counter.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await stranger.GetAsync($"/api/offers/{offer.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ─── withdraw ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_buyer_can_withdraw_a_countered_offer_and_the_seller_cannot()
    {
        var (_, offer) = SeedCountered();
        using var seller = SellerClient();
        using var buyer = BuyerClient();

        (await seller.PostAsync($"/api/offers/{offer.Id}/withdraw", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var response = await buyer.PostAsync($"/api/offers/{offer.Id}/withdraw", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("Withdrawn");
    }

    [Fact]
    public async Task An_unknown_offer_is_not_found()
    {
        using var buyer = BuyerClient();

        using var response = await buyer.PostAsync($"/api/offers/{EntityId.New(MutableClock.Origin)}/withdraw", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ─── idempotency ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_replayed_accept_with_the_same_key_answers_the_same_and_without_one_is_a_conflict()
    {
        var (_, offer) = SeedPending();
        using var seller = SellerClient();
        var key = Guid.NewGuid().ToString();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/offers/{offer.Id}/accept");
            request.Headers.Add("Idempotency-Key", key);
            using var response = await seller.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("Accepted");
        }

        using var plain = await seller.PostAsync($"/api/offers/{offer.Id}/accept", null);
        plain.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await plain.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.already_resolved");
    }

    // ─── expiry ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_lapsed_offer_cannot_be_answered_and_is_saved_as_expired()
    {
        var (_, offer) = SeedPending(createdAt: MutableClock.Origin - TimeSpan.FromDays(3));
        using var seller = SellerClient();

        using var response = await seller.PostAsync($"/api/offers/{offer.Id}/accept", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.expired");
        (await factory.Offers.FindAsync(offer.Id, CancellationToken.None))!.Status.ShouldBe(OfferStatus.Expired);
    }

    [Fact]
    public async Task Lists_show_a_lapsed_offer_as_expired_and_persist_it()
    {
        var (_, offer) = SeedPending(createdAt: MutableClock.Origin - TimeSpan.FromDays(3));
        using var buyer = BuyerClient();

        var row = await GetRowAsync(buyer, offer.Id);

        row.GetProperty("status").GetString().ShouldBe("Expired");
        row.GetProperty("canCheckout").GetBoolean().ShouldBeFalse();
        (await factory.Offers.FindAsync(offer.Id, CancellationToken.None))!.Status.ShouldBe(OfferStatus.Expired);
    }

    [Fact]
    public async Task A_lapsed_open_offer_does_not_stop_the_buyer_making_a_new_one()
    {
        var (listing, _) = SeedPending(createdAt: MutableClock.Origin - TimeSpan.FromDays(3));
        using var buyer = BuyerClient();

        using var response = await buyer.PostAsJsonAsync($"/api/listings/{listing.Id}/offers", new { amountRupiah = 2_100_000 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_new_offer_stores_no_sign_in_names_because_handles_are_resolved_when_read()
    {
        var listing = SeedListing();
        factory.Listings.SellerUsernames[listing.Id] = TestData.SellerUsername;
        using var buyer = BuyerClient();

        using var response = await buyer.PostAsJsonAsync($"/api/listings/{listing.Id}/offers", new { amountRupiah = 2_100_000 });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var offerId = (await response.ReadJsonAsync()).GetProperty("offerId").GetString()!;

        var stored = (await factory.Offers.FindAsync(offerId, CancellationToken.None))!;
        // D-08: sign-in names (cognito:username, which can be an email) are not persisted on the offer;
        // the handles shown to each side are resolved when the offer is read.
        stored.BuyerUsername.ShouldBeNull();
        stored.SellerUsername.ShouldBeNull();
    }

    // ─── reading ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_side_sees_the_listing_the_other_party_and_its_own_role_and_no_user_ids()
    {
        var (listing, offer) = SeedCountered(buyerUsername: "rangga.wh", sellerUsername: "budi.collectibles");
        using var seller = SellerClient();
        using var buyer = BuyerClient();

        var sellerRow = await GetRowAsync(seller, offer.Id);
        var buyerRow = await GetRowAsync(buyer, offer.Id);

        sellerRow.GetProperty("viewerRole").GetString().ShouldBe("seller");
        buyerRow.GetProperty("viewerRole").GetString().ShouldBe("buyer");
        sellerRow.GetProperty("listingTitle").GetString().ShouldBe(listing.Title);
        sellerRow.GetProperty("listingPhotoKey").GetString().ShouldNotBeNullOrEmpty();

        // Public handles resolved from the account ids, never the sign-in names stored on the offer.
        sellerRow.GetProperty("buyerUsername").GetString().ShouldBe(factory.PublicHandleOf(TestData.BidderId));
        buyerRow.GetProperty("sellerUsername").GetString().ShouldBe(factory.PublicHandleOf(TestData.SellerId));
        (await GetRawAsync(seller, offer.Id)).ShouldNotContain("rangga.wh");
        (await GetRawAsync(buyer, offer.Id)).ShouldNotContain("budi.collectibles");
        sellerRow.GetProperty("amountRupiah").GetInt64().ShouldBe(2_000_000);
        sellerRow.GetProperty("counterAmountRupiah").GetInt64().ShouldBe(2_500_000);
        sellerRow.GetProperty("askingPriceRupiah").GetInt64().ShouldBe(3_000_000);
        sellerRow.GetProperty("message").GetString().ShouldBe("Bisa kurang?");

        var raw = sellerRow.GetRawText() + buyerRow.GetRawText();
        raw.ShouldNotContain(TestData.SellerId);
        raw.ShouldNotContain(TestData.BidderId);
    }

    [Fact]
    public async Task The_lists_return_the_callers_own_offers_with_their_role()
    {
        // A buyer of their own, so the shared fake's one-page cap cannot hide the row.
        var buyerId = Guid.NewGuid().ToString();
        var listing = SeedListing();
        var offer = Offer.Create(
            listing.Id, buyerId, listing.SellerId, Money.FromRupiah(2_000_000), listing.Price, null, null,
            new PastClock(MutableClock.Origin), "list.buyer", null);
        factory.Offers.Seed(offer);
        using var buyer = factory.CreateClientAs(buyerId, "list.buyer");

        using var response = await buyer.GetAsync("/api/offers/mine");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var row = (await response.ReadJsonAsync()).GetProperty("items").EnumerateArray().Single();
        row.GetProperty("id").GetString().ShouldBe(offer.Id);
        row.GetProperty("viewerRole").GetString().ShouldBe("buyer");
        row.GetProperty("listingTitle").GetString().ShouldBe(listing.Title);
    }

    [Fact]
    public async Task The_sellers_row_never_carries_the_buyers_order_id()
    {
        var (_, offer) = SeedAccepted();
        offer.MarkCheckedOut(TestData.BidderId, "order-xyz", factory.Clock);
        factory.Offers.Seed(offer);
        using var seller = SellerClient();
        using var buyer = BuyerClient();

        var sellerRow = await GetRowAsync(seller, offer.Id);
        var buyerRow = await GetRowAsync(buyer, offer.Id);

        sellerRow.TryGetProperty("orderId", out _).ShouldBeFalse();
        buyerRow.GetProperty("orderId").GetString().ShouldBe("order-xyz");
    }

    [Fact]
    public async Task Only_an_accepted_offer_on_a_live_listing_can_be_checked_out_by_the_buyer()
    {
        var (_, accepted) = SeedAccepted();
        var (_, pending) = SeedPending();
        using var buyer = BuyerClient();
        using var seller = SellerClient();

        (await GetRowAsync(buyer, accepted.Id)).GetProperty("canCheckout").GetBoolean().ShouldBeTrue();
        (await GetRowAsync(buyer, pending.Id)).GetProperty("canCheckout").GetBoolean().ShouldBeFalse();
        (await GetRowAsync(seller, accepted.Id)).GetProperty("canCheckout").GetBoolean().ShouldBeFalse();
    }

    // ─── realtime ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_transition_pushes_a_signal_to_the_other_party_only()
    {
        var listing = SeedListing();
        using var buyer = BuyerClient();
        using var seller = SellerClient();

        using var made = await buyer.PostAsJsonAsync($"/api/listings/{listing.Id}/offers", new { amountRupiah = 2_000_000 });
        var offerId = (await made.ReadJsonAsync()).GetProperty("offerId").GetString()!;
        PushesFor(TestData.SellerId, offerId).ShouldBe(1);

        (await seller.PostAsJsonAsync($"/api/offers/{offerId}/counter", new { counterAmountRupiah = 2_500_000 }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        PushesFor(TestData.BidderId, offerId).ShouldBe(1);

        (await buyer.PostAsync($"/api/offers/{offerId}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        PushesFor(TestData.SellerId, offerId).ShouldBe(2);

        // The actor is never pushed their own action.
        PushesFor(TestData.BidderId, offerId).ShouldBe(1);
    }

    [Fact]
    public async Task A_failing_push_does_not_fail_the_transition()
    {
        var (_, offer) = SeedPending();
        using var seller = SellerClient();
        factory.Realtime.FailWith = new InvalidOperationException("socket down");
        try
        {
            using var response = await seller.PostAsync($"/api/offers/{offer.Id}/decline", null);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Realtime.FailWith = null;
        }
    }

    // ─── checkout: an accepted offer pays for one order ─────────────────────

    [Fact]
    public async Task Checkout_of_a_countered_then_accepted_offer_charges_the_counter_amount()
    {
        var (_, offer) = SeedCountered();
        using var buyer = BuyerClient();
        (await buyer.PostAsync($"/api/offers/{offer.Id}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(offer.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("grandTotalRupiah").GetInt64().ShouldBe(2_500_000 + 15_000);
    }

    [Fact]
    public async Task An_accepted_offer_can_be_checked_out_only_once()
    {
        var (_, offer) = SeedAccepted();
        using var buyer = BuyerClient();

        using var first = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(offer.Id));
        using var second = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(offer.Id));

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.already_used");

        var stored = (await factory.Offers.FindAsync(offer.Id, CancellationToken.None))!;
        stored.Status.ShouldBe(OfferStatus.CheckedOut);
        stored.OrderId.ShouldBe((await first.ReadJsonAsync()).GetProperty("orderId").GetString());
    }

    [Fact]
    public async Task Checkout_refuses_an_offer_that_is_not_accepted()
    {
        var (_, pending) = SeedPending();
        var (_, countered) = SeedCountered();
        using var buyer = BuyerClient();

        foreach (var offer in new[] { pending, countered })
        {
            using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(offer.Id));
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.not_accepted");
        }
    }

    [Fact]
    public async Task Checkout_refuses_an_accepted_offer_whose_48_hours_have_passed()
    {
        var (_, offer) = SeedAccepted(createdAt: MutableClock.Origin - TimeSpan.FromDays(5));
        using var buyer = BuyerClient();

        using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(offer.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.expired");
    }

    [Fact]
    public async Task Someone_elses_accepted_offer_is_simply_not_found()
    {
        var (_, offer) = SeedAccepted();
        using var other = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var response = await other.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(offer.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_offer_whose_order_was_never_paid_and_lapsed_can_be_checked_out_again()
    {
        var (listing, offer) = SeedAccepted(createdAt: MutableClock.Origin - TimeSpan.FromHours(3));

        // The earlier order was made an hour ago and never paid, so its 15 minutes are long gone.
        var earlier = Order.Create(
            TestData.BidderId, listing.SellerId,
            [new OrderLine(listing.Id, listing.Title, null, offer.CurrentAmount, 1)],
            new ShippingAddress("Rangga Wijaya", "081234567890", "Jl. Merdeka No. 1", "Bandung", "Jawa Barat", "40111"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(15_000), 3),
            new PastClock(MutableClock.Origin - TimeSpan.FromHours(1)));
        factory.Orders.Seed(earlier);
        offer.MarkCheckedOut(TestData.BidderId, earlier.Id, new PastClock(MutableClock.Origin - TimeSpan.FromHours(1)));
        factory.Offers.Seed(offer);
        using var buyer = BuyerClient();

        var row = await GetRowAsync(buyer, offer.Id);
        row.GetProperty("status").GetString().ShouldBe("Accepted");
        row.GetProperty("canCheckout").GetBoolean().ShouldBeTrue();

        using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(offer.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = (await factory.Offers.FindAsync(offer.Id, CancellationToken.None))!;
        stored.Status.ShouldBe(OfferStatus.CheckedOut);
        stored.OrderId.ShouldBe((await response.ReadJsonAsync()).GetProperty("orderId").GetString());
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private HttpClient BuyerClient() => factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

    private HttpClient SellerClient() => factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

    private int PushesFor(string userId, string offerId) => factory.Realtime.Published.Count(push =>
        push.Topic == RealtimeTopics.User(userId)
        && push.Type == "offer"
        && JsonSerializer.Serialize(push.Data).Contains(offerId, StringComparison.Ordinal));

    /// <summary>
    /// One offer as the caller sees it. Read by id rather than out of a list: the fake lists
    /// are capped at one page and every test here shares one buyer and one seller.
    /// </summary>
    private static async Task<JsonElement> GetRowAsync(HttpClient client, string offerId)
    {
        using var response = await client.GetAsync($"/api/offers/{offerId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadJsonAsync();
    }

    private static async Task<string> GetRawAsync(HttpClient client, string offerId)
    {
        using var response = await client.GetAsync($"/api/offers/{offerId}");
        return await response.Content.ReadAsStringAsync();
    }

    private Listing SeedListing()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000), format: ListingFormat.AcceptOffers);
        factory.Listings.Seed(listing);
        return listing;
    }

    private (Listing Listing, Offer Offer) SeedPending(
        DateTimeOffset? createdAt = null, string? buyerUsername = null, string? sellerUsername = null)
    {
        var listing = SeedListing();
        var offer = Offer.Create(
            listing.Id, TestData.BidderId, listing.SellerId, Money.FromRupiah(2_000_000), listing.Price,
            "Bisa kurang?", autoDeclineBelowPercent: null, new PastClock(createdAt ?? MutableClock.Origin),
            buyerUsername, sellerUsername);
        factory.Offers.Seed(offer);
        return (listing, offer);
    }

    private (Listing Listing, Offer Offer) SeedCountered(string? buyerUsername = null, string? sellerUsername = null)
    {
        var (listing, offer) = SeedPending(buyerUsername: buyerUsername, sellerUsername: sellerUsername);
        offer.Counter(Money.FromRupiah(2_500_000), listing.SellerId, factory.Clock);
        factory.Offers.Seed(offer);
        return (listing, offer);
    }

    private (Listing Listing, Offer Offer) SeedAccepted(DateTimeOffset? createdAt = null)
    {
        var (listing, offer) = SeedPending(createdAt);
        offer.Accept(listing.SellerId, new PastClock(createdAt ?? MutableClock.Origin));
        factory.Offers.Seed(offer);
        return (listing, offer);
    }

    private static object CheckoutBody(string offerId) => new
    {
        source = "AcceptedOffer",
        referenceId = offerId,
        shipTo = new
        {
            recipientName = "Rangga Wijaya",
            phone = "081234567890",
            street = "Jl. Merdeka No. 1",
            city = "Bandung",
            province = "Jawa Barat",
            postalCode = "40111",
        },
        courierCode = "jne",
    };

    private sealed class PastClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
