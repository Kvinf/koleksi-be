using System.Collections.Concurrent;
using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An <see cref="IRealtimeBroadcaster"/> that records what was pushed instead of sending it.</summary>
public sealed class FakeRealtimeBroadcaster : FakeAdapter, IRealtimeBroadcaster
{
    private readonly ConcurrentQueue<Published> _published = new();

    public IReadOnlyCollection<Published> Published => _published;

    public Task PublishAsync(string topic, string type, object data, CancellationToken ct)
    {
        ThrowIfFailing();
        _published.Enqueue(new Published(topic, type, data));
        return Task.CompletedTask;
    }
}

public sealed record Published(string Topic, string Type, object Data);

/// <summary>An in-memory <see cref="IRealtimeTicketStore"/>: single use, like the real one.</summary>
public sealed class FakeRealtimeTicketStore : IRealtimeTicketStore
{
    private readonly ConcurrentDictionary<string, string> _tickets = new();

    public Task<string> IssueAsync(string userId, CancellationToken ct)
    {
        var ticket = Guid.NewGuid().ToString("N");
        _tickets[ticket] = userId;
        return Task.FromResult(ticket);
    }

    public Task<string?> ConsumeAsync(string? ticket, CancellationToken ct) =>
        Task.FromResult(ticket is not null && _tickets.TryRemove(ticket, out var userId) ? userId : null);
}
