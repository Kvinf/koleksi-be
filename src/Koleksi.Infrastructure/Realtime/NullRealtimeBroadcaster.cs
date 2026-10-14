using Koleksi.Application.Abstractions;

namespace Koleksi.Infrastructure.Realtime;

/// <summary>
/// Pushes nothing. What a stage gets until <c>Koleksi:Realtime:ManagementUrl</c> is set, and
/// what lets every handler publish unconditionally rather than ask whether anyone could listen.
/// </summary>
/// <remarks>
/// Not an error to be here: the pages poll when no socket is connected, so the product works,
/// just a little later.
/// </remarks>
internal sealed class NullRealtimeBroadcaster : IRealtimeBroadcaster
{
    public Task PublishAsync(string topic, string type, object data, CancellationToken ct) => Task.CompletedTask;
}
