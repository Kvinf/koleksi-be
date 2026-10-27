using Koleksi.Domain.Common;

namespace Koleksi.Domain.Messaging;

/// <summary>One message in a buyer-and-seller conversation about a listing.</summary>
/// <remarks>
/// Immutable once written: there is no edit and no delete in this first version, which is
/// also what makes the thread safe to read with a plain "newer than this id" cursor — a row
/// that can change underneath a poller is a row the poller can miss.
/// </remarks>
public sealed class ChatMessage
{
    /// <summary>
    /// The longest message accepted, in UTF-16 code units.
    /// </summary>
    /// <remarks>
    /// Refused rather than silently cut. An offer's free-text note is truncated, but that is
    /// a courtesy note on a structured action; here the text is the whole point, and a
    /// message that arrives with its last sentence missing is worse than one that was
    /// rejected with a reason the sender can act on. The frontend shows a live counter against
    /// the same number.
    /// </remarks>
    public const int MaxBodyLength = 1000;

    private ChatMessage(string id, string conversationId, string senderId, string body, DateTimeOffset sentAt)
    {
        Id = id;
        ConversationId = conversationId;
        SenderId = senderId;
        Body = body;
        SentAt = sentAt;
    }

    /// <summary>Time-ordered, so ids sort chronologically and double as the polling cursor.</summary>
    public string Id { get; }

    public string ConversationId { get; }

    /// <summary>The Cognito subject of the author. Never sent to the client; see <c>isMine</c>.</summary>
    public string SenderId { get; }

    /// <summary>Plain text. Rendered as text, never as markup.</summary>
    public string Body { get; }

    public DateTimeOffset SentAt { get; }

    /// <summary>Creates a message, applying the length and content rules.</summary>
    /// <exception cref="DomainException">The body is empty or longer than <see cref="MaxBodyLength"/>.</exception>
    public static ChatMessage Create(string conversationId, string senderId, string? body, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);
        ArgumentNullException.ThrowIfNull(clock);

        var normalised = Normalise(body);

        if (normalised.Length == 0)
        {
            throw DomainException.For("chat.empty_message", "A message cannot be empty.");
        }

        if (normalised.Length > MaxBodyLength)
        {
            throw DomainException.For(
                "chat.message_too_long",
                $"A message can be at most {MaxBodyLength} characters.");
        }

        var now = clock.UtcNow;
        return new ChatMessage(EntityId.New(now), conversationId, senderId, normalised, now);
    }

    public static ChatMessage Rehydrate(
        string id, string conversationId, string senderId, string body, DateTimeOffset sentAt) =>
        new(id, conversationId, senderId, body, sentAt);

    /// <summary>
    /// Trims, unifies line endings, and drops control characters other than a line break.
    /// </summary>
    /// <remarks>
    /// Control characters are removed rather than rejected: they arrive from pasted text, not
    /// from anyone trying to say something, and an error for an invisible character is a
    /// message the sender cannot act on. Anything else, including emoji and non-Latin text,
    /// passes through untouched.
    /// </remarks>
    internal static string Normalise(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var unified = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var cleaned = string.Create(unified.Length, unified, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) && source[i] != '\n' ? '\0' : source[i];
            }
        }).Replace("\0", string.Empty, StringComparison.Ordinal);

        return cleaned.Trim();
    }
}
