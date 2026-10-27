using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Messaging;

/// <summary>
/// Sends a buyer's first (or next) message about a listing, creating the conversation if
/// there is not one yet.
/// </summary>
/// <remarks>
/// The buyer comes from <see cref="ICurrentUser"/>, never from the request, for the same
/// reason every other write here follows: a body that named its own sender would let anyone
/// speak as anyone. Pressing "send" twice is harmless — the conversation id is derived from
/// the listing and the buyer, so both land in the same thread.
/// </remarks>
public sealed partial class StartConversationHandler(
    IListingRepository listings,
    IConversationRepository conversations,
    IRealtimeBroadcaster realtime,
    ICurrentUser currentUser,
    IClock clock,
    PublicIdentityResolver identities,
    ILogger<StartConversationHandler> logger)
{
    public async Task<SendMessageResult> HandleAsync(StartConversationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var buyerId = currentUser.RequireUserId();

        var listing = await listings.FindAsync(command.ListingId, ct).ConfigureAwait(false);
        if (listing is null || listing.Status != ListingStatus.Active)
        {
            // Same coarse answer whether the listing is gone, unpublished, or suspended.
            return SendMessageResult.NotFound;
        }

        // An existing thread is kept as it is, so a renamed listing or a changed handle does
        // not rewrite history under a conversation that is already under way.
        var conversation = await conversations
            .FindAsync(Conversation.IdFor(listing.Id, buyerId), ct).ConfigureAwait(false);

        if (conversation is null)
        {
            // Both parties are stored by their public handle, never the sign-in name (an email
            // for most accounts). The inbox re-resolves them on every read, so these copies only
            // matter as a fallback and a record of the names at the time.
            var parties = await identities
                .ResolveManyAsync([buyerId, listing.SellerId], ct).ConfigureAwait(false);
            var buyerUsername = parties[buyerId].Handle;
            var sellerUsername = parties[listing.SellerId].Handle;

            conversation = Conversation.Start(
                listing.Id,
                listing.Title,
                listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null,
                buyerId,
                buyerUsername,
                listing.SellerId,
                sellerUsername,
                clock);
        }

        var message = ChatMessage.Create(conversation.Id, buyerId, command.Body, clock);
        conversation.RecordMessage(message);
        await conversations.AppendAsync(conversation, message, ct).ConfigureAwait(false);
        await ChatEvents.NotifyRecipientAsync(realtime, conversation, buyerId, ex => LogNotifyFailed(logger, conversation.Id, ex), ct)
            .ConfigureAwait(false);

        return new SendMessageResult(Found: true, conversation.Id, message.Id);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Message in conversation {ConversationId} saved, but pushing it to the recipient failed.")]
    private static partial void LogNotifyFailed(ILogger logger, string conversationId, Exception exception);
}

/// <param name="ListingId">The listing the buyer is asking about.</param>
/// <param name="Body">The message text, validated by <see cref="ChatMessage.Create"/>.</param>
public sealed record StartConversationCommand(string ListingId, string? Body);

/// <summary>Sends a message in a conversation the caller is already part of.</summary>
public sealed partial class SendMessageHandler(
    IConversationRepository conversations,
    IRealtimeBroadcaster realtime,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<SendMessageHandler> logger)
{
    public async Task<SendMessageResult> HandleAsync(string conversationId, string? body, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();

        var conversation = await conversations.FindAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !conversation.IsParticipant(userId))
        {
            // Not found and not yours look the same from outside, so a conversation id is not
            // something that can be probed for existence.
            return SendMessageResult.NotFound;
        }

        var message = ChatMessage.Create(conversation.Id, userId, body, clock);
        conversation.RecordMessage(message);
        await conversations.AppendAsync(conversation, message, ct).ConfigureAwait(false);
        await ChatEvents.NotifyRecipientAsync(realtime, conversation, userId, ex => LogNotifyFailed(logger, conversation.Id, ex), ct)
            .ConfigureAwait(false);

        return new SendMessageResult(Found: true, conversation.Id, message.Id);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Message in conversation {ConversationId} saved, but pushing it to the recipient failed.")]
    private static partial void LogNotifyFailed(ILogger logger, string conversationId, Exception exception);
}

/// <summary>
/// The push that tells the other person's open pages a conversation has something new.
/// </summary>
/// <remarks>
/// A signal and nothing more: the conversation id, to the recipient's private topic. The page
/// that receives it reads the message through its ordinary, authenticated loader, so the
/// content is never on the socket and the read still goes through the participant check.
/// Best effort — the message is already saved, and a missed push is caught by the client's
/// next reconnect or its fallback poll.
/// </remarks>
internal static class ChatEvents
{
    public const string ChatEvent = "chat";

    public static async Task NotifyRecipientAsync(
        IRealtimeBroadcaster realtime,
        Conversation conversation,
        string senderId,
        Action<Exception> onFailure,
        CancellationToken ct)
    {
        var recipientId = conversation.RoleOf(senderId) == ConversationRole.Buyer
            ? conversation.SellerId
            : conversation.BuyerId;

        try
        {
            await realtime
                .PublishAsync(RealtimeTopics.User(recipientId), ChatEvent, new { conversationId = conversation.Id }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            onFailure(failure);
        }
    }
}

/// <param name="Found"><c>false</c> when the listing or conversation is not available to the caller.</param>
public sealed record SendMessageResult(bool Found, string? ConversationId, string? MessageId)
{
    public static SendMessageResult NotFound { get; } = new(false, null, null);
}

/// <summary>Reads one conversation and its messages for a participant.</summary>
public sealed class GetConversationHandler(IConversationRepository conversations, ICurrentUser currentUser)
{
    /// <summary>The most messages one read returns, whatever the caller asks for.</summary>
    public const int MaxMessages = 100;

    public const int DefaultMessages = 50;

    public async Task<ConversationThread?> HandleAsync(string conversationId, int? limit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();

        var conversation = await conversations.FindAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !conversation.IsParticipant(userId))
        {
            return null;
        }

        var take = Math.Clamp(limit ?? DefaultMessages, 1, MaxMessages);
        var messages = await conversations.ListMessagesAsync(conversation.Id, take, ct).ConfigureAwait(false);

        return new ConversationThread(conversation, conversation.RoleOf(userId), messages);
    }

    /// <summary>
    /// The caller's own conversation with the seller of <paramref name="listingId"/>, if they
    /// have started one — what the listing page shows under "Ask the seller".
    /// </summary>
    /// <remarks>
    /// The caller is always the buyer here: the id is derived from the listing and the caller,
    /// so there is nothing to look up and nobody else's thread can be named. A seller reading
    /// their own listing has no conversation of their own on it and gets <c>null</c>.
    /// </remarks>
    public Task<ConversationThread?> HandleForListingAsync(string listingId, int? limit, CancellationToken ct) =>
        HandleAsync(Conversation.IdFor(listingId, currentUser.RequireUserId()), limit, ct);
}

public sealed record ConversationThread(
    Conversation Conversation,
    ConversationRole ViewerRole,
    IReadOnlyList<ChatMessage> Messages);

/// <summary>The caller's inbox.</summary>
public sealed class ListConversationsHandler(IConversationRepository conversations, ICurrentUser currentUser)
{
    public const int MaxConversations = 50;

    public async Task<IReadOnlyList<Conversation>> HandleAsync(CancellationToken ct) =>
        await conversations
            .ListForUserAsync(currentUser.RequireUserId(), MaxConversations, ct).ConfigureAwait(false);
}

/// <summary>Marks the caller's side of a conversation as read.</summary>
public sealed class MarkConversationReadHandler(IConversationRepository conversations, ICurrentUser currentUser)
{
    /// <returns><c>false</c> when the conversation is not available to the caller.</returns>
    public async Task<bool> HandleAsync(string conversationId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();

        var conversation = await conversations.FindAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null || !conversation.IsParticipant(userId))
        {
            return false;
        }

        await conversations.MarkReadAsync(conversation.Id, conversation.RoleOf(userId), ct).ConfigureAwait(false);
        return true;
    }
}
