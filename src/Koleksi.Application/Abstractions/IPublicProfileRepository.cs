using Koleksi.Domain.Users;

namespace Koleksi.Application.Abstractions;

/// <summary>
/// Stores each account's public identity and guarantees a handle belongs to one account.
/// </summary>
/// <remarks>
/// A handle is claimed with a conditional write on a lookup row, so two accounts racing for the
/// same name cannot both win, and an account cannot end up holding two. Nothing here reads the
/// private profile.
/// </remarks>
public interface IPublicProfileRepository
{
    Task<PublicProfile?> FindByUserAsync(string userId, CancellationToken ct);

    /// <summary>Several accounts' public profiles in one round trip. Accounts without one are absent from the result.</summary>
    Task<IReadOnlyDictionary<string, PublicProfile>> FindManyByUserAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct);

    /// <summary>The profile that owns <paramref name="handle"/>, or <c>null</c>. The handle must already be canonical.</summary>
    Task<PublicProfile?> FindByHandleAsync(string handle, CancellationToken ct);

    /// <summary>Creates an account's first public profile and claims its handle in one step.</summary>
    Task<PublicProfileWriteResult> CreateAsync(PublicProfile profile, CancellationToken ct);

    /// <summary>
    /// Saves an edited profile, moving the handle claim when <see cref="PublicProfile.Handle"/>
    /// differs from <paramref name="previousHandle"/>.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The profile changed since it was read.</exception>
    Task<PublicProfileWriteResult> SaveAsync(
        PublicProfile profile, string previousHandle, long expectedVersion, CancellationToken ct);
}

/// <summary>How a public-profile write ended.</summary>
public enum PublicProfileWriteResult
{
    /// <summary>The write took effect.</summary>
    Written = 0,

    /// <summary>Another account already holds the handle.</summary>
    HandleTaken = 1,

    /// <summary>The account already has a public profile (a create that lost a race with itself).</summary>
    AlreadyExists = 2,
}
