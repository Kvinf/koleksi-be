using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Messaging;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>A buyer and a seller talking about a listing, end to end.</summary>
public sealed class ConversationEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    // A fresh buyer per test (xUnit builds one instance per test). The messaging bucket is per user
    // and shared across the whole fixture, so tests posting as one buyer would drain it for each other.
    private readonly string _buyerId = Guid.NewGuid().ToString();
    private readonly string _buyerName = "buyer." + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task A_buyer_can_message_the_seller_from_a_listing_and_the_seller_sees_it_unread()
    {
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var started = await buyer.PostAsJsonAsync(
            "/api/conversations", new { listingId = listing.Id, body = "Masih ada kak?" });

        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        var conversationId = (await started.ReadJsonAsync()).GetProperty("conversationId").GetString();
        conversationId.ShouldBe(Conversation.IdFor(listing.Id, _buyerId));

        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        using var inbox = await seller.GetAsync("/api/conversations");

        inbox.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await inbox.ReadJsonAsync();
        var row = body.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == conversationId);
        row.GetProperty("unread").GetInt32().ShouldBe(1);
        row.GetProperty("otherPartyUsername").GetString().ShouldBe(factory.PublicHandleOf(_buyerId));
        row.GetProperty("lastMessagePreview").GetString().ShouldBe("Masih ada kak?");
        row.GetProperty("listingTitle").GetString().ShouldBe(listing.Title);
        body.GetProperty("totalUnread").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Messaging_the_same_listing_twice_lands_in_one_conversation()
    {
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var first = await buyer.PostAsJsonAsync("/api/conversations", new { listingId = listing.Id, body = "Halo" });
        using var second = await buyer.PostAsJsonAsync("/api/conversations", new { listingId = listing.Id, body = "Ada kak?" });

        var firstId = (await first.ReadJsonAsync()).GetProperty("conversationId").GetString();
        var secondId = (await second.ReadJsonAsync()).GetProperty("conversationId").GetString();
        secondId.ShouldBe(firstId);

        using var thread = await buyer.GetAsync($"/api/conversations/{firstId}");
        (await thread.ReadJsonAsync()).GetProperty("messages").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A_listing_that_does_not_exist_is_not_found()
    {
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var response = await buyer.PostAsJsonAsync(
            "/api/conversations", new { listingId = TestData.UnknownListingId, body = "Halo" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("chat.listing_not_found");
    }

    [Fact]
    public async Task An_unpublished_listing_cannot_be_messaged_about()
    {
        var draft = TestData.DraftListing();
        factory.Listings.Seed(draft);
        factory.Listings.SellerUsernames[draft.Id] = TestData.SellerUsername;
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var response = await buyer.PostAsJsonAsync(
            "/api/conversations", new { listingId = draft.Id, body = "Halo" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_seller_cannot_start_a_conversation_about_their_own_listing()
    {
        var listing = SeedActiveListing();
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await seller.PostAsJsonAsync(
            "/api/conversations", new { listingId = listing.Id, body = "Tes" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("chat.self_conversation");
    }

    [Theory]
    [InlineData("", "chat.empty_message")]
    [InlineData("   ", "chat.empty_message")]
    public async Task An_empty_message_is_refused(string body, string code)
    {
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var response = await buyer.PostAsJsonAsync("/api/conversations", new { listingId = listing.Id, body });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task A_message_over_the_limit_is_refused_and_not_stored()
    {
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var response = await buyer.PostAsJsonAsync(
            "/api/conversations",
            new { listingId = listing.Id, body = new string('a', ChatMessage.MaxBodyLength + 1) });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("chat.message_too_long");

        var stored = await factory.Conversations.FindAsync(
            Conversation.IdFor(listing.Id, _buyerId), CancellationToken.None);
        stored.ShouldBeNull();
    }

    [Fact]
    public async Task The_seller_can_reply_and_each_side_sees_whose_message_is_whose()
    {
        var conversationId = await StartConversationAsync("Masih ada kak?");
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var reply = await seller.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages", new { body = "Ada, silakan" });
        reply.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);
        using var buyerView = await buyer.GetAsync($"/api/conversations/{conversationId}");
        var buyerBody = await buyerView.ReadJsonAsync();

        buyerBody.GetProperty("viewerRole").GetString().ShouldBe("Buyer");
        buyerBody.GetProperty("conversation").GetProperty("unread").GetInt32().ShouldBe(1);
        buyerBody.GetProperty("conversation").GetProperty("otherPartyUsername").GetString()
            .ShouldBe(factory.PublicHandleOf(TestData.SellerId));
        buyerBody.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("body").GetString(), m.GetProperty("isMine").GetBoolean()))
            .ShouldBe([("Masih ada kak?", true), ("Ada, silakan", false)]);

        using var sellerView = await seller.GetAsync($"/api/conversations/{conversationId}");
        (await sellerView.ReadJsonAsync()).GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("isMine").GetBoolean())
            .ShouldBe([false, true]);
    }

    [Fact]
    public async Task Responses_never_carry_a_user_id()
    {
        var conversationId = await StartConversationAsync("Halo");
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        var thread = await (await buyer.GetAsync($"/api/conversations/{conversationId}")).Content.ReadAsStringAsync();
        var inbox = await (await buyer.GetAsync("/api/conversations")).Content.ReadAsStringAsync();

        foreach (var text in new[] { thread, inbox })
        {
            text.ShouldNotContain(TestData.SellerId);
            text.ShouldNotContain(_buyerId);
        }
    }

    [Fact]
    public async Task A_stranger_cannot_read_write_or_mark_read_and_cannot_tell_it_exists()
    {
        var conversationId = await StartConversationAsync("Rahasia");
        using var stranger = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var read = await stranger.GetAsync($"/api/conversations/{conversationId}");
        using var write = await stranger.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages", new { body = "Nyelonong" });
        using var mark = await stranger.PostAsync($"/api/conversations/{conversationId}/read", null);
        using var missing = await stranger.GetAsync("/api/conversations/00000000000000000000000000000000");

        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        write.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        mark.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The refused write left nothing behind.
        var stored = await factory.Conversations.ListMessagesAsync(conversationId, 10, CancellationToken.None);
        stored.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Opening_a_conversation_clears_only_the_openers_unread_count()
    {
        var conversationId = await StartConversationAsync("Halo");
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var marked = await seller.PostAsync($"/api/conversations/{conversationId}/read", null);
        marked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var sellerView = await seller.GetAsync($"/api/conversations/{conversationId}");
        (await sellerView.ReadJsonAsync()).GetProperty("conversation").GetProperty("unread").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Reading_a_thread_does_not_mark_it_read()
    {
        // A poll is a GET and must have no side effects, or a thread open in a background tab
        // would silently swallow every notification.
        var conversationId = await StartConversationAsync("Halo");
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var first = await seller.GetAsync($"/api/conversations/{conversationId}");
        using var second = await seller.GetAsync($"/api/conversations/{conversationId}");

        (await second.ReadJsonAsync()).GetProperty("conversation").GetProperty("unread").GetInt32().ShouldBe(1);
        _ = first;
    }

    [Fact]
    public async Task The_inbox_lists_the_most_recently_active_conversation_first()
    {
        var older = SeedActiveListing();
        var newer = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var firstStart = await buyer.PostAsJsonAsync("/api/conversations", new { listingId = older.Id, body = "A" });
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using var secondStart = await buyer.PostAsJsonAsync("/api/conversations", new { listingId = newer.Id, body = "B" });

        using var inbox = await buyer.GetAsync("/api/conversations");
        var ids = (await inbox.ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("listingId").GetString()).ToList();

        ids.IndexOf(newer.Id).ShouldBeLessThan(ids.IndexOf(older.Id));
        _ = (firstStart, secondStart);
    }

    [Fact]
    public async Task Resubmitting_a_message_with_the_same_idempotency_key_stores_it_once()
    {
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);
        var key = Guid.NewGuid().ToString();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversations")
            {
                Content = JsonContent.Create(new { listingId = listing.Id, body = "Sekali saja" }),
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await buyer.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var stored = await factory.Conversations.ListMessagesAsync(
            Conversation.IdFor(listing.Id, _buyerId), 10, CancellationToken.None);
        stored.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sending_faster_than_the_budget_allows_is_rate_limited()
    {
        // Its own caller, so the bucket this drains is not one another test is spending.
        const string caller = "rate-limited-caller-sub";
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(caller, "rate.limited");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 14; i++)
        {
            using var response = await buyer.PostAsJsonAsync(
                "/api/conversations", new { listingId = listing.Id, body = $"pesan {i}" });
            statuses.Add(response.StatusCode);
        }

        statuses.Take(10).ShouldAllBe(status => status == HttpStatusCode.OK);
        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_buyer_can_read_their_own_conversation_straight_from_the_listing()
    {
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);
        using var started = await buyer.PostAsJsonAsync(
            "/api/conversations", new { listingId = listing.Id, body = "Masih ada kak?" });
        var conversationId = (await started.ReadJsonAsync()).GetProperty("conversationId").GetString();

        using var response = await buyer.GetAsync($"/api/listings/{listing.Id}/conversation");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("conversation").GetProperty("id").GetString().ShouldBe(conversationId);
        body.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("body").GetString())
            .ShouldBe(["Masih ada kak?"]);
    }

    [Fact]
    public async Task A_visitor_who_never_messaged_has_no_conversation_on_the_listing()
    {
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var response = await buyer.GetAsync($"/api/listings/{listing.Id}/conversation");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Someone_elses_conversation_cannot_be_reached_through_the_listing()
    {
        // The id is derived from the caller, so there is no way to name another buyer's thread:
        // the seller asking for the same listing's conversation asks for their own, which does
        // not exist, and a second buyer asks for theirs.
        var conversationId = await StartConversationAsync("Rahasia");
        _ = conversationId;
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);
        using var started = await buyer.PostAsJsonAsync("/api/conversations", new { listingId = listing.Id, body = "Halo" });
        started.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        using var rival = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        (await seller.GetAsync($"/api/listings/{listing.Id}/conversation")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await rival.GetAsync($"/api/listings/{listing.Id}/conversation")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_everywhere()
    {
        using var anonymous = factory.CreateClient();
        var id = Conversation.IdFor(TestData.UnknownListingId, _buyerId);

        (await anonymous.GetAsync("/api/conversations")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/listings/{TestData.UnknownListingId}/conversation"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/conversations/{id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/conversations", new { listingId = "x", body = "y" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync($"/api/conversations/{id}/messages", new { body = "y" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync($"/api/conversations/{id}/read", null))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private Listing SeedActiveListing()
    {
        var listing = TestData.ActiveListing();
        factory.Listings.Seed(listing);
        factory.Listings.SellerUsernames[listing.Id] = TestData.SellerUsername;
        return listing;
    }

    private async Task<string> StartConversationAsync(string body)
    {
        var listing = SeedActiveListing();
        using var buyer = factory.CreateClientAs(_buyerId, _buyerName);

        using var response = await buyer.PostAsJsonAsync("/api/conversations", new { listingId = listing.Id, body });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.ReadJsonAsync()).GetProperty("conversationId").GetString()!;
    }
}
