using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IIdempotencyStore"/>.
/// </summary>
/// <remarks>
/// Claims are scoped per caller in production so one user cannot collide with, or probe
/// for, another's key. The composite key here keeps that property, so a test that relies
/// on it is testing the same rule the DynamoDB store enforces with a conditional write.
/// Retention is honoured too, against the same clock the host uses: a claim past it counts as
/// absent, exactly as in the DynamoDB store, and so is a claim with no result that has outlived
/// <see cref="IdempotencyExtensions.InFlightLease"/>.
/// All access is locked, because the whole point of the tests that use this is parallel requests
/// racing for one key, and a plain dictionary would corrupt itself rather than lose the race.
/// </remarks>
public sealed class FakeIdempotencyStore(IClock clock) : FakeAdapter, IIdempotencyStore
{
    private readonly object _gate = new();

    private readonly Dictionary<(string Scope, string Key), Claim> _claims = [];

    public Task<bool> TryClaimAsync(string scope, string key, TimeSpan retention, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (_gate)
        {
            var now = clock.UtcNow;
            if (_claims.TryGetValue((scope, key), out var existing)
                && existing.ExpiresAt > now
                && (existing.Result is not null || existing.LeaseUntil > now))
            {
                return Task.FromResult(false);
            }

            _claims[(scope, key)] = new Claim(null, now + retention, now + IdempotencyExtensions.InFlightLease);
            return Task.FromResult(true);
        }
    }

    public Task RecordResultAsync(string scope, string key, string resultJson, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (_gate)
        {
            var existing = _claims.TryGetValue((scope, key), out var claim)
                ? claim
                : new Claim(null, clock.UtcNow, clock.UtcNow);
            _claims[(scope, key)] = existing with { Result = resultJson };
            return Task.CompletedTask;
        }
    }

    public Task<string?> GetRecordedResultAsync(string scope, string key, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (_gate)
        {
            return Task.FromResult(
                _claims.TryGetValue((scope, key), out var claim) && claim.ExpiresAt > clock.UtcNow
                    ? claim.Result
                    : null);
        }
    }

    public Task ReleaseClaimAsync(string scope, string key, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (_gate)
        {
            if (_claims.TryGetValue((scope, key), out var claim) && claim.Result is null)
            {
                _claims.Remove((scope, key));
            }

            return Task.CompletedTask;
        }
    }

    private sealed record Claim(string? Result, DateTimeOffset ExpiresAt, DateTimeOffset LeaseUntil);
}
