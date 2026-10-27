using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Users;

namespace Koleksi.Application.Features.Sellers;

/// <summary>Reads the caller's own public identity, assigning a handle if they have none yet.</summary>
public sealed class GetOwnPublicProfileHandler(PublicIdentityResolver identities, ICurrentUser currentUser, IClock clock)
{
    public async Task<OwnPublicProfileView> HandleAsync(CancellationToken ct)
    {
        var profile = await identities.ResolveAsync(currentUser.RequireUserId(), ct).ConfigureAwait(false);
        return OwnPublicProfileView.From(profile, clock.UtcNow);
    }
}

/// <summary>Changes the caller's handle and/or display name.</summary>
public sealed class UpdateOwnPublicProfileHandler(
    IPublicProfileRepository profiles,
    PublicIdentityResolver identities,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<OwnPublicProfileView> HandleAsync(string? handle, string? displayName, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();

        // Make sure the account has a profile to edit (first-time callers are assigned one), then
        // edit a fresh copy: the resolver's cached instance is shared and must never be mutated.
        await identities.ResolveAsync(userId, ct).ConfigureAwait(false);
        var profile = await profiles.FindByUserAsync(userId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A public profile that was just resolved could not be read back.");

        var previousHandle = profile.Handle;
        var expectedVersion = profile.Version;

        // Throws the rule that was broken: handle.invalid_format, handle.reserved,
        // handle.change_too_soon or profile.display_name_invalid.
        profile.Edit(handle, displayName, clock);

        var result = await profiles.SaveAsync(profile, previousHandle, expectedVersion, ct).ConfigureAwait(false);
        if (result == PublicProfileWriteResult.HandleTaken)
        {
            throw DomainException.For("handle.taken", "That handle is already in use.");
        }

        identities.Invalidate(userId);
        return OwnPublicProfileView.From(profile, clock.UtcNow);
    }
}

/// <param name="CanChangeHandleAfter">When the handle may next be changed; <c>null</c> when it can be changed now.</param>
public sealed record OwnPublicProfileView(
    string Handle,
    string? DisplayName,
    DateTimeOffset MemberSince,
    DateTimeOffset? CanChangeHandleAfter)
{
    public static OwnPublicProfileView From(PublicProfile profile, DateTimeOffset now)
    {
        DateTimeOffset? after = profile.HandleChangedAt is { } changed && changed + PublicProfile.HandleChangeCooldown > now
            ? changed + PublicProfile.HandleChangeCooldown
            : null;

        return new OwnPublicProfileView(profile.Handle, profile.DisplayName, profile.MemberSince, after);
    }
}
