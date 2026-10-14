using Koleksi.Domain.Common;
using Koleksi.Domain.Messaging;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class MessagingTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";
    private const string Stranger = "stranger-sub";
    private const string ListingId = "01JQ8ZC4K7N2M5V9XR3P6TW0YB";

    private static Conversation StartConversation(TestClock? clock = null) => Conversation.Start(
        ListingId, "PSA 10 Charizard", "listings/a/b", Buyer, "rangga.wh", Seller, "budi.collectibles",
        clock ?? TestClock.AtEpoch());

    private static ChatMessage Message(Conversation conversation, string sender, string body) =>
        ChatMessage.Create(conversation.Id, sender, body, TestClock.AtEpoch());

    // ─── Message rules ──────────────────────────────────────────────────────────

    [Fact]
    public void A_message_is_trimmed_and_keeps_its_text()
    {
        var message = Message(StartConversation(), Buyer, "  Masih ada kak?  ");

        message.Body.ShouldBe("Masih ada kak?");
        message.SenderId.ShouldBe(Buyer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    public void An_empty_message_is_refused(string? body) =>
        Should.Throw<DomainException>(() => Message(StartConversation(), Buyer, body!))
            .Code.ShouldBe("chat.empty_message");

    [Fact]
    public void A_message_of_exactly_the_limit_is_accepted() =>
        Message(StartConversation(), Buyer, new string('a', ChatMessage.MaxBodyLength))
            .Body.Length.ShouldBe(ChatMessage.MaxBodyLength);

    [Fact]
    public void A_message_over_the_limit_is_refused_rather_than_cut() =>
        Should.Throw<DomainException>(() =>
                Message(StartConversation(), Buyer, new string('a', ChatMessage.MaxBodyLength + 1)))
            .Code.ShouldBe("chat.message_too_long");

    [Fact]
    public void The_limit_applies_after_trimming_so_padding_cannot_push_a_message_over()
    {
        var padded = "  " + new string('a', ChatMessage.MaxBodyLength) + "  ";

        Message(StartConversation(), Buyer, padded).Body.Length.ShouldBe(ChatMessage.MaxBodyLength);
    }

    [Fact]
    public void Control_characters_are_removed_and_line_breaks_are_kept_and_unified()
    {
        var message = Message(StartConversation(), Buyer, "baris satu\r\nbaris\u0000 dua\u0007\rbaris tiga");

        message.Body.ShouldBe("baris satu\nbaris dua\nbaris tiga");
    }

    [Fact]
    public void Emoji_and_non_latin_text_pass_through_untouched()
    {
        const string text = "Boleh nego? 🙏 カード";

        Message(StartConversation(), Buyer, text).Body.ShouldBe(text);
    }

    [Fact]
    public void Markup_is_not_interpreted_it_is_stored_as_the_text_it_is()
    {
        // Rendering as plain text is the frontend's job; the domain's is not to mangle it.
        Message(StartConversation(), Buyer, "<script>alert(1)</script>")
            .Body.ShouldBe("<script>alert(1)</script>");
    }

    [Fact]
    public void Message_ids_are_time_ordered_across_milliseconds()
    {
        var conversation = StartConversation();
        var earlier = ChatMessage.Create(conversation.Id, Buyer, "satu", new TestClock(TestClock.AtEpoch().UtcNow));
        var later = ChatMessage.Create(
            conversation.Id, Buyer, "dua", new TestClock(TestClock.AtEpoch().UtcNow.AddMilliseconds(5)));

        string.CompareOrdinal(earlier.Id, later.Id).ShouldBeLessThan(0);
    }

    // ─── Starting a conversation ────────────────────────────────────────────────

    [Fact]
    public void A_buyer_cannot_start_a_conversation_about_their_own_listing() =>
        Should.Throw<DomainException>(() => Conversation.Start(
                ListingId, "Title", null, Seller, "budi", Seller, "budi", TestClock.AtEpoch()))
            .Code.ShouldBe("chat.self_conversation");

    [Fact]
    public void The_same_buyer_and_listing_always_give_the_same_conversation()
    {
        // This is what makes a double-tapped "send" land in one thread with no create race.
        StartConversation().Id.ShouldBe(StartConversation().Id);
        Conversation.IdFor(ListingId, Buyer).ShouldBe(StartConversation().Id);
    }

    [Fact]
    public void The_conversation_id_does_not_reveal_the_buyer_or_the_listing()
    {
        // The id is shown to the seller and sits in URLs. Concatenating the buyer's Cognito
        // subject into it would hand the seller that subject on every response.
        var id = StartConversation().Id;

        id.ShouldNotContain(Buyer);
        id.ShouldNotContain(ListingId);
        id.Length.ShouldBe(32);
        id.ShouldAllBe(c => char.IsAsciiHexDigitLower(c));
    }

    [Fact]
    public void The_halves_of_the_id_cannot_be_shifted_to_collide() =>
        Conversation.IdFor("ab", "c").ShouldNotBe(Conversation.IdFor("a", "bc"));

    [Fact]
    public void A_different_buyer_gets_a_different_conversation_on_the_same_listing() =>
        Conversation.IdFor(ListingId, Stranger).ShouldNotBe(Conversation.IdFor(ListingId, Buyer));

    [Fact]
    public void A_new_conversation_starts_with_nothing_unread_on_either_side()
    {
        var conversation = StartConversation();

        conversation.BuyerUnread.ShouldBe(0);
        conversation.SellerUnread.ShouldBe(0);
        conversation.LastMessagePreview.ShouldBeEmpty();
    }

    // ─── Participants ───────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_buyer_and_the_seller_are_participants()
    {
        var conversation = StartConversation();

        conversation.IsParticipant(Buyer).ShouldBeTrue();
        conversation.IsParticipant(Seller).ShouldBeTrue();
        conversation.IsParticipant(Stranger).ShouldBeFalse();
    }

    [Fact]
    public void A_stranger_has_no_role()
    {
        Should.Throw<DomainException>(() => StartConversation().RoleOf(Stranger))
            .Code.ShouldBe("chat.forbidden");
    }

    [Fact]
    public void Each_side_sees_the_other_sides_handle()
    {
        var conversation = StartConversation();

        conversation.OtherPartyUsername(Buyer).ShouldBe("budi.collectibles");
        conversation.OtherPartyUsername(Seller).ShouldBe("rangga.wh");
    }

    // ─── Recording a message ────────────────────────────────────────────────────

    [Fact]
    public void A_buyers_message_is_unread_for_the_seller_and_clears_the_buyers_own_count()
    {
        var conversation = StartConversation();

        conversation.RecordMessage(Message(conversation, Buyer, "Masih ada?"));

        conversation.SellerUnread.ShouldBe(1);
        conversation.BuyerUnread.ShouldBe(0);
        conversation.UnreadFor(Seller).ShouldBe(1);
        conversation.UnreadFor(Buyer).ShouldBe(0);
        conversation.LastSenderId.ShouldBe(Buyer);
        conversation.LastMessagePreview.ShouldBe("Masih ada?");
    }

    [Fact]
    public void Several_unanswered_messages_accumulate_for_the_other_side()
    {
        var conversation = StartConversation();

        conversation.RecordMessage(Message(conversation, Buyer, "satu"));
        conversation.RecordMessage(Message(conversation, Buyer, "dua"));
        conversation.RecordMessage(Message(conversation, Buyer, "tiga"));

        conversation.SellerUnread.ShouldBe(3);
    }

    [Fact]
    public void A_reply_clears_the_repliers_unread_and_raises_the_other_sides()
    {
        var conversation = StartConversation();
        conversation.RecordMessage(Message(conversation, Buyer, "Masih ada?"));

        conversation.RecordMessage(Message(conversation, Seller, "Ada kak"));

        conversation.SellerUnread.ShouldBe(0);
        conversation.BuyerUnread.ShouldBe(1);
        conversation.LastSenderId.ShouldBe(Seller);
    }

    [Fact]
    public void A_stranger_cannot_post_into_a_conversation()
    {
        var conversation = StartConversation();

        Should.Throw<DomainException>(() => conversation.RecordMessage(Message(conversation, Stranger, "halo")))
            .Code.ShouldBe("chat.forbidden");
    }

    // ─── Inbox preview ──────────────────────────────────────────────────────────

    [Fact]
    public void The_preview_is_one_line_and_cut_to_its_length()
    {
        var conversation = StartConversation();

        conversation.RecordMessage(Message(conversation, Buyer, "baris satu\n\nbaris dua\n" + new string('x', 200)));

        conversation.LastMessagePreview.ShouldStartWith("baris satu baris dua ");
        conversation.LastMessagePreview.ShouldNotContain('\n');
        conversation.LastMessagePreview.Length.ShouldBeLessThanOrEqualTo(Conversation.PreviewLength);
    }

    [Fact]
    public void The_preview_never_cuts_an_emoji_in_half()
    {
        var conversation = StartConversation();
        // Place a surrogate pair so its first half is the last character that would be kept.
        var body = new string('a', Conversation.PreviewLength - 1) + "😀" + "tail";

        conversation.RecordMessage(Message(conversation, Buyer, body));

        conversation.LastMessagePreview.ShouldBe(new string('a', Conversation.PreviewLength - 1));
        char.IsHighSurrogate(conversation.LastMessagePreview[^1]).ShouldBeFalse();
    }
}
