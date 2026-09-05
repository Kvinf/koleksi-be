using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IIdempotencyStore"/>.
/// </summary>
/// <remarks>
/// Claims are scoped per caller in production so one user cannot collide with, or probe
/// for, another's key. The composite key here keeps that property, so a test that relies
/// on it is testing the same rule the DynamoDB store enforces with a conditional write.
/// </remarks>
public sealed class FakeIdempotencyStore : FakeAdapter, IIdempotencyStore
{
    private readonly Dictionary<(string Scope, string Key), string?> _claims = [];

    public Task<bool> TryClaimAsync(string scope, string key, TimeSpan retention, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(_claims.TryAdd((scope, key), null));
    }

    public Task RecordResultAsync(string scope, string key, string resultJson, CancellationToken ct)
    {
        ThrowIfFailing();
        _claims[(scope, key)] = resultJson;
        return Task.CompletedTask;
    }

    public Task<string?> GetRecordedResultAsync(string scope, string key, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(_claims.GetValueOrDefault((scope, key)));
    }
}
