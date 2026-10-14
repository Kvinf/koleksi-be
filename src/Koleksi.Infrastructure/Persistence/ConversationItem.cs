using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Messaging;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Maps a <see cref="Conversation"/>'s summary row to and from its stored item.</summary>
/// <remarks>
/// Read-only in this direction: the row is never written whole. The repository changes it with
/// <c>UpdateItem</c> (see <see cref="DynamoConversationRepository.AppendAsync"/>), so a
/// conversation's unread counters are only ever moved by atomic adds. There is deliberately no
/// <c>ToItem</c> here for a caller to reach for and overwrite them with.
/// </remarks>
internal static class ConversationItem
{
    public const string TypeName = "Conversation";

    public const string IdAttr = "id";
    public const string ListingIdAttr = "listingId";
    public const string ListingTitleAttr = "listingTitle";
    public const string ListingPhotoKeyAttr = "listingPhotoKey";
    public const string BuyerIdAttr = "buyerId";
    public const string BuyerUsernameAttr = "buyerUsername";
    public const string SellerIdAttr = "sellerId";
    public const string SellerUsernameAttr = "sellerUsername";
    public const string CreatedAtAttr = "createdAt";
    public const string LastMessageAtAttr = "lastMessageAt";
    public const string LastPreviewAttr = "lastMessagePreview";
    public const string LastSenderIdAttr = "lastSenderId";
    public const string BuyerUnreadAttr = "buyerUnread";
    public const string SellerUnreadAttr = "sellerUnread";

    public static string UnreadAttr(ConversationRole role) =>
        role == ConversationRole.Buyer ? BuyerUnreadAttr : SellerUnreadAttr;

    public static Conversation ToDomain(Dictionary<string, AttributeValue> item) => Conversation.Rehydrate(
        id: item.GetString(IdAttr),
        listingId: item.GetString(ListingIdAttr),
        listingTitle: item.GetString(ListingTitleAttr),
        listingPhotoKey: item.GetStringOrNull(ListingPhotoKeyAttr),
        buyerId: item.GetString(BuyerIdAttr),
        buyerUsername: item.GetString(BuyerUsernameAttr),
        sellerId: item.GetString(SellerIdAttr),
        sellerUsername: item.GetString(SellerUsernameAttr),
        createdAt: item.GetInstant(CreatedAtAttr),
        lastMessageAt: item.GetInstant(LastMessageAtAttr),
        lastMessagePreview: item.GetStringOrNull(LastPreviewAttr) ?? string.Empty,
        lastSenderId: item.GetStringOrNull(LastSenderIdAttr),
        buyerUnread: UnreadCount(item, BuyerUnreadAttr),
        sellerUnread: UnreadCount(item, SellerUnreadAttr));

    // An absent counter is zero: a side that has never been messaged has nothing stored yet.
    private static int UnreadCount(Dictionary<string, AttributeValue> item, string name) =>
        (int)Math.Clamp(item.GetLongOrDefault(name), 0, int.MaxValue);
}

/// <summary>Maps a <see cref="ChatMessage"/> to and from its stored item.</summary>
internal static class ChatMessageItem
{
    public const string TypeName = "ChatMessage";

    private const string IdAttr = "id";
    private const string ConversationIdAttr = "conversationId";
    private const string SenderIdAttr = "senderId";
    private const string BodyAttr = "body";
    private const string SentAtAttr = "sentAt";

    public static Dictionary<string, AttributeValue> ToItem(ChatMessage message) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ConversationPk(message.ConversationId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.MessageSk(message.Id)),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(message.Id),
            [ConversationIdAttr] = Attr.S(message.ConversationId),
            [SenderIdAttr] = Attr.S(message.SenderId),
            [BodyAttr] = Attr.S(message.Body),
            [SentAtAttr] = Attr.Instant(message.SentAt),
        };

    public static ChatMessage ToDomain(Dictionary<string, AttributeValue> item) => ChatMessage.Rehydrate(
        id: item.GetString(IdAttr),
        conversationId: item.GetString(ConversationIdAttr),
        senderId: item.GetString(SenderIdAttr),
        body: item.GetString(BodyAttr),
        sentAt: item.GetInstant(SentAtAttr));
}
