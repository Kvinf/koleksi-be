using Koleksi.Application.Abstractions;
using Koleksi.Domain.Users;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IProfileRepository"/>.
/// </summary>
/// <remarks>
/// Reads and writes copy through <see cref="UserProfile.Rehydrate"/>, same reasoning as
/// <see cref="FakeListingRepository"/>: a fake that shared the stored instance would let a
/// handler's mutation appear "saved" without the write path — and its version check —
/// ever running.
/// </remarks>
public sealed class FakeProfileRepository : FakeAdapter, IProfileRepository
{
    private readonly Dictionary<string, UserProfile> _profiles = new(StringComparer.Ordinal);

    public Task<UserProfile?> FindAsync(string userId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(_profiles.TryGetValue(userId, out var profile) ? Copy(profile) : null);
    }

    public Task AddAsync(UserProfile profile, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(profile);

        if (!_profiles.TryAdd(profile.Id, Copy(profile)))
        {
            throw new ConcurrencyConflictException($"Account '{profile.Id}' already has a profile.");
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(UserProfile profile, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(profile);

        if (!_profiles.TryGetValue(profile.Id, out var stored) || stored.Version != expectedVersion)
        {
            throw new ConcurrencyConflictException($"Profile for '{profile.Id}' changed concurrently.");
        }

        _profiles[profile.Id] = Copy(profile);
        return Task.CompletedTask;
    }

    private static UserProfile Copy(UserProfile profile) => UserProfile.Rehydrate(
        profile.Id,
        profile.FullName,
        profile.Phone,
        profile.PhoneVerified,
        profile.AvatarKey,
        profile.IdVerificationStatus,
        profile.BankAccount,
        profile.Addresses,
        profile.CreatedAt,
        profile.UpdatedAt,
        profile.Version,
        profile.IdDocumentKey);
}
