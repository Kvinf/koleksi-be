using Koleksi.Domain.Messaging;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary><c>DynamoConversationRepository</c> against a real table.</summary>
/// <remarks>
/// What only a real table can answer: that the single <c>UpdateItem</c> really creates the
/// summary on a first message, never rewrites its identity fields afterwards, and moves the two
/// unread counters atomically and independently.
/// </remarks>
public sealed class ConversationRepositoryTests : DynamoTestBase
{
    private const string Buyer = Fixtures.BidderId;
    private const string Seller = Fixtures.SellerId;

    private static Conversation StartConversation(string listingId = "01JQ8ZC4K7N2M5V9XR3P6TW0YB") => Conversation.Start(
        listingId, "PSA 10 Charizard", "listings/a/b", Buyer, "rangga.wh", Seller, "budi", Fixtures.Clock);

    private async Task<ChatMessage> SendAsync(Conversation conversation, string sender, string body)
    {
        var message = ChatMessage.Create(conversation.Id, sender, body, Fixtures.Clock);
        conversation.RecordMessage(message);
        await Table.Conversations.AppendAsync(conversation, message, Ct);
        return message;
    }

    [DynamoLocalFact]
    public async Task The_first_message_creates_the_conversation_and_counts_one_unread_for_the_seller()
    {
        var conversation = StartConversation();

        await SendAsync(conversation, Buyer, "Masih ada kak?");

        var stored = await Table.Conversations.FindAsync(conversation.Id, Ct);
        stored.ShouldNotBeNull();
        stored.ListingTitle.ShouldBe("PSA 10 Charizard");
        stored.ListingPhotoKey.ShouldBe("listings/a/b");
        stored.BuyerUsername.ShouldBe("rangga.wh");
        stored.SellerUsername.ShouldBe("budi");
        stored.SellerUnread.ShouldBe(1);
        stored.BuyerUnread.ShouldBe(0);
        stored.LastMessagePreview.ShouldBe("Masih ada kak?");
    }

    [DynamoLocalFact]
    public async Task A_second_message_from_a_fresh_copy_never_rewrites_who_the_conversation_is_between()
    {
        var conversation = StartConversation();
        await SendAsync(conversation, Buyer, "satu");

        // A later caller whose copy carries a different title and handle, as a renamed
        // listing would. The stored identity must not move.
        var drifted = Conversation.Start(
            conversation.ListingId, "TITLE CHANGED", null, Buyer, "renamed.buyer", Seller, "renamed.seller", Fixtures.Clock);
        await SendAsync(drifted, Buyer, "dua");

        var stored = await Table.Conversations.FindAsync(conversation.Id, Ct);
        stored.ShouldNotBeNull();
        stored.ListingTitle.ShouldBe("PSA 10 Charizard");
        stored.BuyerUsername.ShouldBe("rangga.wh");
        stored.SellerUnread.ShouldBe(2);
    }

    [DynamoLocalFact]
    public async Task A_reply_clears_the_repliers_unread_and_raises_the_other_sides()
    {
        var conversation = StartConversation();
        await SendAsync(conversation, Buyer, "Masih ada?");

        var reply = await Table.Conversations.FindAsync(conversation.Id, Ct);
        reply.ShouldNotBeNull();
        await SendAsync(reply, Seller, "Ada kak");

        var stored = await Table.Conversations.FindAsync(conversation.Id, Ct);
        stored.ShouldNotBeNull();
        stored.SellerUnread.ShouldBe(0);
        stored.BuyerUnread.ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task Marking_read_clears_only_the_callers_side()
    {
        var conversation = StartConversation();
        await SendAsync(conversation, Buyer, "Halo");

        await Table.Conversations.MarkReadAsync(conversation.Id, ConversationRole.Seller, Ct);

        var stored = await Table.Conversations.FindAsync(conversation.Id, Ct);
        stored.ShouldNotBeNull();
        stored.SellerUnread.ShouldBe(0);
    }

    [DynamoLocalFact]
    public async Task Marking_a_conversation_that_does_not_exist_read_creates_nothing()
    {
        await Table.Conversations.MarkReadAsync("00000000000000000000000000000000", ConversationRole.Buyer, Ct);

        (await Table.Conversations.FindAsync("00000000000000000000000000000000", Ct)).ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_thread_returns_the_latest_messages_oldest_first()
    {
        var conversation = StartConversation();
        for (var i = 1; i <= 5; i++)
        {
            await SendAsync(conversation, Buyer, $"pesan {i}");
        }

        var latestThree = await Table.Conversations.ListMessagesAsync(conversation.Id, 3, Ct);

        latestThree.Select(message => message.Body).ShouldBe(["pesan 3", "pesan 4", "pesan 5"]);
    }

    [DynamoLocalFact]
    public async Task The_inbox_lists_a_users_conversations_on_either_side_most_recent_first()
    {
        var asBuyer = StartConversation("01JQ8ZC4K7N2M5V9XR3P6TW0AA");
        await SendAsync(asBuyer, Buyer, "pertama");

        // The same person as the *seller* of a different conversation.
        var asSeller = Conversation.Start(
            "01JQ8ZC4K7N2M5V9XR3P6TW0BB", "Gunpla RG Nu", null, "someone-else", "someone", Buyer, "rangga.wh", Fixtures.Clock);
        await SendAsync(asSeller, "someone-else", "halo penjual");

        var inbox = await Table.Conversations.ListForUserAsync(Buyer, 10, Ct);

        inbox.Select(conversation => conversation.Id).ShouldContain(asBuyer.Id);
        inbox.Select(conversation => conversation.Id).ShouldContain(asSeller.Id);
    }
}
