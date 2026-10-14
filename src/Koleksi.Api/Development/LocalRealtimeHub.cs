#if DEBUG
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure.Realtime;

namespace Koleksi.Api.Development;

/// <summary>
/// The development stand-in for API Gateway's WebSocket API: the same wire protocol, held in
/// this one process.
/// </summary>
/// <remarks>
/// <para>
/// A browser connects to <c>/realtime</c>, optionally with <c>?ticket=</c> (redeemed through the
/// same <see cref="IRealtimeTicketStore"/> a deployed stage uses), and sends
/// <c>{"action":"subscribe","topic":"auction:…"}</c> frames. This hub then relays every
/// <see cref="IRealtimeBroadcaster.PublishAsync"/> to the matching connections. Because the
/// browser-facing protocol and the frames are the ones the deployed path speaks, the web
/// tier's client is exercised identically here — what differs is only who holds the sockets.
/// </para>
/// <para>
/// Not a production path and not shaped like one: one process means no fan-out across
/// instances, which is exactly why a deployed stage uses API Gateway. Compiled into Debug
/// builds only and wired by <c>Program.cs</c> only in the Development environment with no
/// <c>Koleksi:Realtime:ManagementUrl</c> set, the same gate as every other local stand-in.
/// </para>
/// </remarks>
internal sealed partial class LocalRealtimeHub(IRealtimeTicketStore tickets, ILogger<LocalRealtimeHub> logger)
    : IRealtimeBroadcaster
{
    /// <summary>The largest client frame accepted; the protocol's own are well under 200 bytes.</summary>
    private const int MaxFrameBytes = 1024;

    private readonly ConcurrentDictionary<Connection, byte> connections = new();

    public async Task PublishAsync(string topic, string type, object data, CancellationToken ct)
    {
        var frame = RealtimeEnvelope.Encode(topic, type, data);

        foreach (var connection in connections.Keys)
        {
            if (!connection.Receives(topic))
            {
                continue;
            }

            try
            {
                await connection.SendAsync(frame, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is WebSocketException or ObjectDisposedException)
            {
                // The browser went away between the check and the send; its read loop will
                // remove it.
                LogSendFailed(logger, failure);
            }
        }
    }

    /// <summary>Serves one WebSocket connection until it closes.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string? userId = null;
        var ticket = context.Request.Query["ticket"].ToString();
        if (ticket.Length > 0)
        {
            userId = await tickets.ConsumeAsync(ticket, context.RequestAborted).ConfigureAwait(false);
            if (userId is null)
            {
                // A ticket that does not redeem is refused outright rather than downgraded to
                // an anonymous connection, so the client knows to fetch a fresh one.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var connection = new Connection(socket, userId);
        connections[connection] = 0;

        try
        {
            await ReadLoopAsync(connection, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is WebSocketException or OperationCanceledException)
        {
            // An abrupt disconnect is the normal way a browser tab ends.
        }
        finally
        {
            connections.TryRemove(connection, out _);
        }
    }

    private static async Task ReadLoopAsync(Connection connection, CancellationToken ct)
    {
        var buffer = new byte[MaxFrameBytes + 1];

        while (connection.Socket.State == WebSocketState.Open)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;

            do
            {
                result = await connection.Socket
                    .ReceiveAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await connection.Socket
                        .CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }

                length += result.Count;
            }
            while (!result.EndOfMessage && length <= MaxFrameBytes);

            if (length > MaxFrameBytes)
            {
                await connection.Socket
                    .CloseAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            connection.Handle(buffer.AsSpan(0, length));
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "A local realtime connection closed before an event could be sent.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception);

    private sealed class Connection(WebSocket socket, string? userId) : IDisposable
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly HashSet<string> topics = new(StringComparer.Ordinal);

        public WebSocket Socket { get; } = socket;

        public void Dispose() => sendLock.Dispose();

        public bool Receives(string topic)
        {
            if (userId is not null && string.Equals(topic, RealtimeTopics.User(userId), StringComparison.Ordinal))
            {
                return true;
            }

            lock (topics)
            {
                return topics.Contains(topic);
            }
        }

        /// <summary>Applies one client frame: subscribe, unsubscribe, or ping. Anything else is ignored.</summary>
        public void Handle(ReadOnlySpan<byte> frame)
        {
            string? action;
            string? topic;

            try
            {
                var reader = new Utf8JsonReader(frame);
                using var document = JsonDocument.ParseValue(ref reader);
                action = document.RootElement.TryGetProperty("action", out var a) ? a.GetString() : null;
                topic = document.RootElement.TryGetProperty("topic", out var t) ? t.GetString() : null;
            }
            catch (Exception failure) when (failure is JsonException or InvalidOperationException)
            {
                return;
            }

            lock (topics)
            {
                switch (action)
                {
                    case "subscribe" when RealtimeTopics.IsSubscribable(topic)
                        && (topics.Count < RealtimeTopics.MaxSubscriptionsPerConnection || topics.Contains(topic!)):
                        topics.Add(topic!);
                        break;

                    case "unsubscribe" when topic is not null:
                        topics.Remove(topic);
                        break;
                }
            }
        }

        public async Task SendAsync(byte[] frame, CancellationToken ct)
        {
            // One send at a time per socket: the framework forbids overlapping sends.
            await sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                sendLock.Release();
            }
        }
    }
}
#endif
