using Koleksi.Domain.Messaging;

namespace Koleksi.Application.Abstractions;

/// <summary>Reads and writes buyer-and-seller conversations and their messages.</summary>
/// <remarks>
/// Deliberately has no "update this conversation" method. A conversation changes in exactly
/// two ways — a message is appended, or a participant opens it — and both are atomic storage
/// operations on counters rather than a read-modify-write of the whole item, so two people
/// replying at the same moment cannot overwrite each other's unread counts.
/// </remarks>
public interface IConversationRepository
{
    Task<Conversation?> FindAsync(string conversationId, CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="message"/> and moves the conversation summary forward in one
    /// transaction, creating the conversation if this is its first message.
    /// </summary>
    /// <remarks>
    /// <paramref name="conversation"/> must already have had the message applied with
    /// <see cref="Conversation.RecordMessage"/>. The summary fields written are the ones that
    /// instance now holds; the recipient's unread count is advanced by an atomic add.
    /// </remarks>
    Task AppendAsync(Conversation conversation, ChatMessage message, CancellationToken ct);

    /// <summary>
    /// The most recent <paramref name="limit"/> messages of a conversation, oldest first, read
    /// strongly consistently.
    /// </summary>
    /// <remarks>
    /// Deliberately not a "newer than the last id I saw" cursor. Ids are only ordered to the
    /// millisecond, and a write can commit after one with a later id already has, so a poller
    /// holding a cursor could skip a message permanently when two people send together. Reading
    /// the latest page every time cannot miss one, and at a few dozen small items per read it
    /// costs next to nothing. Consistent, so a message the caller just sent is in the answer.
    /// </remarks>
    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(string conversationId, int limit, CancellationToken ct);

    /// <summary>Zeroes one side's unread count. Does nothing if the conversation does not exist.</summary>
    Task MarkReadAsync(string conversationId, ConversationRole role, CancellationToken ct);

    /// <summary>
    /// Every conversation the user is in, as buyer or as seller, most recently active first.
    /// </summary>
    /// <remarks>
    /// Bounded by <paramref name="limit"/> and not paged: an inbox past a few dozen threads is
    /// a product question (archiving, search), not one a cursor answers.
    /// </remarks>
    Task<IReadOnlyList<Conversation>> ListForUserAsync(string userId, int limit, CancellationToken ct);
}
