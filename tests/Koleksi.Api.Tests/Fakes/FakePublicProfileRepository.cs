using Koleksi.Application.Abstractions;
using Koleksi.Domain.Users;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IPublicProfileRepository"/> that enforces the same two guarantees the
/// transactional write does: one profile per account, one account per handle.
/// </summary>
public sealed class FakePublicProfileRepository : FakeAdapter, IPublicProfileRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PublicProfile> _byUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _handleOwners = new(StringComparer.Ordinal);

    /// <summary>Handles left by a rename, held for their old owner until the instant (see PublicProfile.HandleReservationPeriod).</summary>
    private readonly Dictionary<string, (string Owner, DateTimeOffset Until)> _reserved = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="handle"/> is free for <paramref name="userId"/> to claim at <paramref name="now"/>.</summary>
    private bool IsClaimable(string handle, string userId, DateTimeOffset now) =>
        !_handleOwners.ContainsKey(handle)
        && (!_reserved.TryGetValue(handle, out var held)
            || held.Owner == userId
            || held.Until < now);

    public Task<PublicProfile?> FindByUserAsync(string userId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(_byUser.TryGetValue(userId, out var profile) ? Copy(profile) : null);
        }
    }

    public Task<IReadOnlyDictionary<string, PublicProfile>> FindManyByUserAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyDictionary<string, PublicProfile> found = userIds
                .Where(_byUser.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(id => id, id => Copy(_byUser[id]), StringComparer.Ordinal);
            return Task.FromResult(found);
        }
    }

    public Task<PublicProfile?> FindByHandleAsync(string handle, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(
                _handleOwners.TryGetValue(handle, out var owner) ? Copy(_byUser[owner]) : null);
        }
    }

    public Task<PublicProfileWriteResult> CreateAsync(PublicProfile profile, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (_byUser.ContainsKey(profile.UserId))
            {
                return Task.FromResult(PublicProfileWriteResult.AlreadyExists);
            }

            if (!IsClaimable(profile.Handle, profile.UserId, profile.UpdatedAt))
            {
                return Task.FromResult(PublicProfileWriteResult.HandleTaken);
            }

            _reserved.Remove(profile.Handle);
            _byUser[profile.UserId] = Copy(profile);
            _handleOwners[profile.Handle] = profile.UserId;
            return Task.FromResult(PublicProfileWriteResult.Written);
        }
    }

    public Task<PublicProfileWriteResult> SaveAsync(
        PublicProfile profile, string previousHandle, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (!_byUser.TryGetValue(profile.UserId, out var stored) || stored.Version != expectedVersion)
            {
                throw new ConcurrencyConflictException($"Public profile for '{profile.UserId}' changed concurrently.");
            }

            if (!string.Equals(profile.Handle, previousHandle, StringComparison.Ordinal))
            {
                if (!IsClaimable(profile.Handle, profile.UserId, profile.UpdatedAt))
                {
                    return Task.FromResult(PublicProfileWriteResult.HandleTaken);
                }

                _reserved.Remove(profile.Handle);
                _handleOwners.Remove(previousHandle);
                _reserved[previousHandle] = (profile.UserId, profile.UpdatedAt + PublicProfile.HandleReservationPeriod);
                _handleOwners[profile.Handle] = profile.UserId;
            }

            _byUser[profile.UserId] = Copy(profile);
            return Task.FromResult(PublicProfileWriteResult.Written);
        }
    }

    /// <summary>Seeds a profile directly, bypassing the write path.</summary>
    public void Seed(PublicProfile profile) => CreateAsync(profile, CancellationToken.None).GetAwaiter().GetResult();

    private static PublicProfile Copy(PublicProfile p) => PublicProfile.Rehydrate(
        p.UserId, p.Handle, p.DisplayName, p.MemberSince, p.HandleChangedAt, p.UpdatedAt, p.Version);
}
