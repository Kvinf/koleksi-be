using System.Security.Cryptography;
using System.Text;
using Koleksi.Domain.Common;

namespace Koleksi.Domain.Messaging;

/// <summary>Which side of a conversation a participant is on.</summary>
public enum ConversationRole
{
    Buyer = 0,
    Seller = 1,
}

/// <summary>
/// A buyer's thread with a listing's seller. There is exactly one per buyer per listing.
/// </summary>
/// <remarks>
/// <para>
/// The id is derived from the listing and the buyer (<see cref="IdFor"/>), not generated, and
/// is opaque: it does not contain either of them.
/// "Message the seller" pressed twice, from two tabs, or after a retry therefore lands in
/// the same thread by construction, with no lookup-then-create race to get wrong.
/// </para>
/// <para>
/// Usernames and the listing title are copied in when the thread starts. User ids are Cognito
/// subjects and nothing in this product can turn one back into a handle, and a thread must
/// still be readable after its listing is sold or archived. Both are display snapshots, not
/// sources of truth.
/// </para>
/// <para>
/// The unread counters here describe what was true when this instance was read. They are
/// advanced by an atomic add in storage, never by writing this object back, so two people
/// messaging at once cannot overwrite each other's counts.
/// </para>
/// </remarks>
public sealed class Conversation
{
    /// <summary>Characters of the latest message kept for the inbox row.</summary>
    public const int PreviewLength = 80;

    private Conversation(
        string id,
        string listingId,
        string listingTitle,
        string? listingPhotoKey,
        string buyerId,
        string buyerUsername,
        string sellerId,
        string sellerUsername,
        DateTimeOffset createdAt)
    {
        Id = id;
        ListingId = listingId;
        ListingTitle = listingTitle;
        ListingPhotoKey = listingPhotoKey;
        BuyerId = buyerId;
        BuyerUsername = buyerUsername;
        SellerId = sellerId;
        SellerUsername = sellerUsername;
        CreatedAt = createdAt;
        LastMessageAt = createdAt;
    }

    public string Id { get; }

    public string ListingId { get; }

    public string ListingTitle { get; }

    public string? ListingPhotoKey { get; }

    public string BuyerId { get; }

    public string BuyerUsername { get; }

    public string SellerId { get; }

    public string SellerUsername { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastMessageAt { get; private set; }

    /// <summary>The start of the newest message, for the inbox row. Empty until one is sent.</summary>
    public string LastMessagePreview { get; private set; } = string.Empty;

    public string? LastSenderId { get; private set; }

    public int BuyerUnread { get; private set; }

    public int SellerUnread { get; private set; }

    /// <summary>The conversation id for a listing and the buyer asking about it.</summary>
    /// <remarks>
    /// A hash of the two, not their concatenation. The id travels in URLs and in every response
    /// to both participants, and a concatenation would hand the seller the buyer's Cognito
    /// subject. A hash is still derivable from the pair alone — which is the point — while
    /// revealing neither half. It is a name, not a secret: every route still authorises the
    /// caller as a participant.
    /// </remarks>
    public static string IdFor(string listingId, string buyerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        // A newline separates the halves so ("ab", "c") and ("a", "bc") cannot hash alike;
        // neither a listing id nor a Cognito subject can contain one.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{listingId}\n{buyerId}"));

        // 128 bits is far beyond what any collision across this marketplace needs.
        return Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    /// <summary>Begins a conversation. Only a buyer starts one; a seller replies to it.</summary>
    /// <exception cref="DomainException">The buyer is the listing's own seller.</exception>
    public static Conversation Start(
        string listingId,
        string listingTitle,
        string? listingPhotoKey,
        string buyerId,
        string buyerUsername,
        string sellerId,
        string sellerUsername,
        IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(listingTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerUsername);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.Equals(buyerId, sellerId, StringComparison.Ordinal))
        {
            throw DomainException.For(
                "chat.self_conversation", "You cannot start a conversation about your own listing.");
        }

        return new Conversation(
            IdFor(listingId, buyerId),
            listingId,
            listingTitle,
            listingPhotoKey,
            buyerId,
            buyerUsername,
            sellerId,
            sellerUsername,
            clock.UtcNow);
    }

    public static Conversation Rehydrate(
        string id,
        string listingId,
        string listingTitle,
        string? listingPhotoKey,
        string buyerId,
        string buyerUsername,
        string sellerId,
        string sellerUsername,
        DateTimeOffset createdAt,
        DateTimeOffset lastMessageAt,
        string lastMessagePreview,
        string? lastSenderId,
        int buyerUnread,
        int sellerUnread) =>
        new(id, listingId, listingTitle, listingPhotoKey, buyerId, buyerUsername, sellerId, sellerUsername, createdAt)
        {
            LastMessageAt = lastMessageAt,
            LastMessagePreview = lastMessagePreview,
            LastSenderId = lastSenderId,
            BuyerUnread = buyerUnread,
            SellerUnread = sellerUnread,
        };

    public bool IsParticipant(string userId) =>
        string.Equals(userId, BuyerId, StringComparison.Ordinal)
        || string.Equals(userId, SellerId, StringComparison.Ordinal);

    /// <summary>Which side <paramref name="userId"/> is on.</summary>
    /// <exception cref="DomainException">The user is in neither side of this conversation.</exception>
    public ConversationRole RoleOf(string userId)
    {
        if (string.Equals(userId, BuyerId, StringComparison.Ordinal))
        {
            return ConversationRole.Buyer;
        }

        if (string.Equals(userId, SellerId, StringComparison.Ordinal))
        {
            return ConversationRole.Seller;
        }

        throw DomainException.For("chat.forbidden", "You are not part of this conversation.");
    }

    /// <summary>The other participant's handle, as the viewer should see it.</summary>
    public string OtherPartyUsername(string viewerId) =>
        RoleOf(viewerId) == ConversationRole.Buyer ? SellerUsername : BuyerUsername;

    /// <summary>How many messages the viewer has not opened yet.</summary>
    public int UnreadFor(string viewerId) =>
        RoleOf(viewerId) == ConversationRole.Buyer ? BuyerUnread : SellerUnread;

    /// <summary>
    /// Records <paramref name="message"/> as the newest, from a participant, in this instance.
    /// </summary>
    /// <remarks>
    /// Updates the in-memory summary so the caller can hand it to storage. The unread counters
    /// are advanced here only so this instance reads sensibly afterwards; storage applies the
    /// same change as an atomic add.
    /// </remarks>
    /// <exception cref="DomainException">The sender is not part of this conversation.</exception>
    public void RecordMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var senderRole = RoleOf(message.SenderId);

        LastMessageAt = message.SentAt;
        LastSenderId = message.SenderId;
        LastMessagePreview = Preview(message.Body);

        if (senderRole == ConversationRole.Buyer)
        {
            BuyerUnread = 0;
            SellerUnread++;
        }
        else
        {
            SellerUnread = 0;
            BuyerUnread++;
        }
    }

    internal static string Preview(string body)
    {
        var singleLine = string.Join(' ', body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (singleLine.Length <= PreviewLength)
        {
            return singleLine;
        }

        // Do not cut an emoji in half: a lone high surrogate renders as a replacement glyph.
        var end = char.IsHighSurrogate(singleLine[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength;
        return singleLine[..end];
    }
}
