using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>Key composition for conversations and their messages.</summary>
public sealed class ConversationKeyTests
{
    private const string ConversationId = "9f2c4e7a1b3d4f60a8c5e1d27b3f9a04";

    [Fact]
    public void A_conversation_and_its_messages_share_one_partition()
    {
        // One partition is what lets a message and the summary it advances commit together,
        // and a whole thread be a single Query.
        DynamoTable.ConversationPk(ConversationId).ShouldBe("CONV#" + ConversationId);
        DynamoTable.ConversationMetaSk().ShouldBe("#META");
        DynamoTable.MessageSk("01JQ8ZC4K7N2M5V9XR3P6TW0YC").ShouldStartWith(DynamoTable.MessageSkPrefix());
    }

    [Fact]
    public void The_summary_row_sorts_outside_the_message_range()
    {
        // The latest-page read is begins_with(sk, "MSG#"); the summary must never fall inside it.
        string.CompareOrdinal(DynamoTable.ConversationMetaSk(), DynamoTable.MessageSkPrefix()).ShouldNotBe(0);
        DynamoTable.ConversationMetaSk().ShouldNotStartWith(DynamoTable.MessageSkPrefix());
    }

    [Fact]
    public void Later_messages_sort_after_earlier_ones()
    {
        var earlier = DynamoTable.MessageSk("01JQ8ZC4K7N2M5V9XR3P6TW0YB");
        var later = DynamoTable.MessageSk("01JQ8ZC4K8N2M5V9XR3P6TW0YB");

        string.CompareOrdinal(earlier, later).ShouldBeLessThan(0);
    }

    [Fact]
    public void The_inbox_index_key_sorts_by_latest_activity_not_by_creation()
    {
        var older = DynamoTable.BuyerSkValue(
            DynamoTable.ConversationIndexPrefix(), "2026-03-01T09:00:00.0000000Z", "A");
        var newer = DynamoTable.BuyerSkValue(
            DynamoTable.ConversationIndexPrefix(), "2026-03-01T09:05:00.0000000Z", "A");

        string.CompareOrdinal(older, newer).ShouldBeLessThan(0);
        older.ShouldStartWith(DynamoTable.ConversationIndexPrefix());
    }

    [Fact]
    public void Conversation_index_rows_do_not_collide_with_offer_or_order_rows_in_the_shared_indexes()
    {
        // Offers, orders and conversations are all multiplexed onto gsi_buyer / gsi_seller by
        // type prefix. Distinct prefixes are the only thing keeping an inbox query from
        // returning an order.
        DynamoTable.ConversationIndexPrefix().ShouldNotBe(DynamoTable.OfferIndexPrefix());
        DynamoTable.ConversationIndexPrefix().ShouldNotBe(DynamoTable.OrderIndexPrefix());
        DynamoTable.OfferIndexPrefix().ShouldNotStartWith(DynamoTable.ConversationIndexPrefix());
        DynamoTable.OrderIndexPrefix().ShouldNotStartWith(DynamoTable.ConversationIndexPrefix());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_key_component_is_refused(string component)
    {
        Should.Throw<ArgumentException>(() => DynamoTable.ConversationPk(component));
        Should.Throw<ArgumentException>(() => DynamoTable.MessageSk(component));
    }
}
