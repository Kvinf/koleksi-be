using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Realtime;

/// <summary>
/// The in-process implementation of <see cref="IAuctionEventPublisher"/>, and the
/// subscription side <c>AuctionEndpoints</c>' event-stream route reads from.
/// </summary>
/// <remarks>
/// One channel per open connection, grouped by listing id. A publish fans out to every
/// channel subscribed to that listing; a channel with no reader left (its request has
/// ended) is dropped from the group when its own read loop exits, not by the publisher —
/// the publisher never blocks on a slow or gone reader.
/// </remarks>
public sealed class AuctionEventHub : IAuctionEventPublisher
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<AuctionPriceUpdate>, byte>> subscribers =
        new(StringComparer.Ordinal);

    public Task PublishAsync(string listingId, AuctionPriceUpdate update, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentNullException.ThrowIfNull(update);

        if (subscribers.TryGetValue(listingId, out var channels))
        {
            foreach (var channel in channels.Keys)
            {
                // Unbounded and non-blocking: a reader that has fallen behind must never
                // make PublishAsync — called from inside the bid-handling path — wait on it.
                channel.Writer.TryWrite(update);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Streams every update published for <paramref name="listingId"/> until the caller's
    /// connection ends.
    /// </summary>
    public async IAsyncEnumerable<AuctionPriceUpdate> SubscribeAsync(
        string listingId, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var channel = Channel.CreateUnbounded<AuctionPriceUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var group = subscribers.GetOrAdd(listingId, _ => new ConcurrentDictionary<Channel<AuctionPriceUpdate>, byte>());
        group[channel] = 0;

        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            group.TryRemove(channel, out _);
        }
    }
}
