using Koleksi.Application.Abstractions;
using Koleksi.Domain.Messaging;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IConversationRepository"/> that behaves the way the DynamoDB one does:
/// the summary is created once and its identity fields never change, and each send advances the
/// recipient's unread count by one and zeroes the sender's, rather than storing whatever the
/// caller's copy happens to say.
/// </summary>
public sealed class FakeConversationRepository : FakeAdapter, IConversationRepository
{
    private readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ChatMessage>> _messages = new(StringComparer.Ordinal);

    public Task<Conversation?> FindAsync(string conversationId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(_conversations.TryGetValue(conversationId, out var stored) ? Copy(stored) : null);
    }

    public Task AppendAsync(Conversation conversation, ChatMessage message, CancellationToken ct)
    {
        ThrowIfFailing();

        var senderRole = conversation.RoleOf(message.SenderId);
        var existing = _conversations.GetValueOrDefault(conversation.Id);

        var buyerUnread = existing?.BuyerUnread ?? 0;
        var sellerUnread = existing?.SellerUnread ?? 0;
        if (senderRole == ConversationRole.Buyer)
        {
            buyerUnread = 0;
            sellerUnread++;
        }
        else
        {
            sellerUnread = 0;
            buyerUnread++;
        }

        // Identity fields come from the stored row when there is one, as if_not_exists does.
        var source = existing ?? conversation;
        _conversations[conversation.Id] = Conversation.Rehydrate(
            source.Id, source.ListingId, source.ListingTitle, source.ListingPhotoKey,
            source.BuyerId, source.BuyerUsername, source.SellerId, source.SellerUsername,
            source.CreatedAt, message.SentAt, conversation.LastMessagePreview, message.SenderId,
            buyerUnread, sellerUnread);

        if (!_messages.TryGetValue(conversation.Id, out var thread))
        {
            thread = [];
            _messages[conversation.Id] = thread;
        }

        thread.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(string conversationId, int limit, CancellationToken ct)
    {
        ThrowIfFailing();

        IReadOnlyList<ChatMessage> latest = _messages.TryGetValue(conversationId, out var thread)
            ? thread.TakeLast(limit).ToList()
            : [];
        return Task.FromResult(latest);
    }

    public Task MarkReadAsync(string conversationId, ConversationRole role, CancellationToken ct)
    {
        ThrowIfFailing();

        if (_conversations.TryGetValue(conversationId, out var stored))
        {
            _conversations[conversationId] = Conversation.Rehydrate(
                stored.Id, stored.ListingId, stored.ListingTitle, stored.ListingPhotoKey,
                stored.BuyerId, stored.BuyerUsername, stored.SellerId, stored.SellerUsername,
                stored.CreatedAt, stored.LastMessageAt, stored.LastMessagePreview, stored.LastSenderId,
                role == ConversationRole.Buyer ? 0 : stored.BuyerUnread,
                role == ConversationRole.Seller ? 0 : stored.SellerUnread);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Conversation>> ListForUserAsync(string userId, int limit, CancellationToken ct)
    {
        ThrowIfFailing();

        IReadOnlyList<Conversation> mine = _conversations.Values
            .Where(conversation => conversation.IsParticipant(userId))
            .OrderByDescending(conversation => conversation.LastMessageAt)
            .Take(limit)
            .Select(Copy)
            .ToList();
        return Task.FromResult(mine);
    }

    private static Conversation Copy(Conversation c) => Conversation.Rehydrate(
        c.Id, c.ListingId, c.ListingTitle, c.ListingPhotoKey, c.BuyerId, c.BuyerUsername, c.SellerId,
        c.SellerUsername, c.CreatedAt, c.LastMessageAt, c.LastMessagePreview, c.LastSenderId,
        c.BuyerUnread, c.SellerUnread);
}
