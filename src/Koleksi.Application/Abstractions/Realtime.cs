namespace Koleksi.Application.Abstractions;

/// <summary>
/// Pushes an event to every browser connection currently subscribed to a topic.
/// </summary>
/// <remarks>
/// <para>
/// Best-effort by contract: a failure here must never fail the write that triggered it, so
/// callers catch and log. Delivery is also at-most-once and unordered across connections —
/// a missed event is repaired by the client re-reading the page's data when its socket
/// reconnects, never by anything on this side.
/// </para>
/// <para>
/// Events carry only what the topic's audience is already allowed to see. A <c>user:</c>
/// topic goes to one person and carries a signal ("this conversation changed"), not the
/// content, so private data never rides the socket.
/// </para>
/// </remarks>
public interface IRealtimeBroadcaster
{
    /// <param name="topic">A topic built by <see cref="RealtimeTopics"/>.</param>
    /// <param name="type">The event name the browser switches on, e.g. <c>price</c>.</param>
    /// <param name="data">Serialised to camelCase JSON.</param>
    Task PublishAsync(string topic, string type, object data, CancellationToken ct);
}

/// <summary>
/// Single-use, short-lived tickets that bind a WebSocket connection to a signed-in user.
/// </summary>
/// <remarks>
/// The browser holds no bearer token (the session lives in a cookie only the web tier can
/// read), and a WebSocket handshake cannot carry a custom header anyway. So the web tier
/// asks the API for a ticket on the user's behalf and hands it to the browser, which presents
/// it once when it connects. A ticket is a random value that is only ever stored as a hash.
/// </remarks>
public interface IRealtimeTicketStore
{
    /// <summary>How long an issued ticket stays redeemable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <returns>The ticket to hand to the browser.</returns>
    Task<string> IssueAsync(string userId, CancellationToken ct);

    /// <summary>Redeems a ticket, which can never be redeemed again.</summary>
    /// <returns>The user it was issued to, or <c>null</c> when it is unknown, used, or expired.</returns>
    Task<string?> ConsumeAsync(string? ticket, CancellationToken ct);
}

/// <summary>The topic names, and the one rule about which of them a browser may ask for.</summary>
/// <remarks>
/// A browser may subscribe only to <c>auction:</c> topics, which are public. A <c>user:</c>
/// topic is joined by the server when a ticket is redeemed and can never be requested, so
/// there is no way to ask for somebody else's.
/// </remarks>
public static class RealtimeTopics
{
    private const string AuctionPrefix = "auction:";
    private const string UserPrefix = "user:";

    /// <summary>The most topics one connection may subscribe to.</summary>
    public const int MaxSubscriptionsPerConnection = 10;

    public static string Auction(string listingId) => AuctionPrefix + Require(listingId);

    public static string User(string userId) => UserPrefix + Require(userId);

    /// <summary>Whether a browser is allowed to subscribe to <paramref name="topic"/>.</summary>
    public static bool IsSubscribable(string? topic)
    {
        if (topic is null || !topic.StartsWith(AuctionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var id = topic.AsSpan(AuctionPrefix.Length);
        if (id.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The topic name the browser sees: a user topic is just "user", never an id.</summary>
    public static string WireName(string topic) =>
        topic.StartsWith(UserPrefix, StringComparison.Ordinal) ? "user" : topic;

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
