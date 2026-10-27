using System.Collections.Concurrent;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Sellers;

/// <summary>
/// Turns an account id into the name the public may see, at read time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why at read time.</b> Listings, browse cards, conversations, bids and the cart already
/// carry the sign-in name from when they were written, and for an email account that is the
/// email address. Rather than rewrite every stored row, every public or counter-party-facing
/// response asks this resolver for the name of the account id it is about, so old rows stop
/// leaking the moment this code is deployed and a later handle change shows up everywhere.
/// </para>
/// <para>
/// <b>An account with no handle gets one on first need.</b> The ids resolved here come from our
/// own rows (a listing's seller, a conversation's parties, a bid's bidder), never from a
/// request, so this cannot be driven to mint handles for accounts that do not exist.
/// </para>
/// <para>
/// A short in-process cache keeps a page of twelve cards from costing twelve reads. It is
/// per-instance: after a rename, other warm instances can show the old handle for up to
/// <see cref="CacheLifetime"/>. That is the price of not reading on every card.
/// </para>
/// </remarks>
public sealed partial class PublicIdentityResolver(
    IPublicProfileRepository profiles,
    IProfileRepository accounts,
    IClock clock,
    ILogger<PublicIdentityResolver> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    /// <summary>A hard ceiling so a long-lived instance cannot grow the cache without bound.</summary>
    private const int MaxCachedEntries = 5_000;

    /// <summary>Draws before giving up. A collision in a 31^8 space is vanishingly rare; five in a row is a bug.</summary>
    private const int MaxClaimAttempts = 5;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>The public identity of one account, assigning a handle if it has none yet.</summary>
    public async Task<PublicProfile> ResolveAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var resolved = await ResolveManyAsync([userId], ct).ConfigureAwait(false);
        return resolved[userId];
    }

    /// <summary>The public identities of several accounts, keyed by account id.</summary>
    public async Task<IReadOnlyDictionary<string, PublicProfile>> ResolveManyAsync(
        IEnumerable<string> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var wanted = userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, PublicProfile>(wanted.Count, StringComparer.Ordinal);
        var now = clock.UtcNow;

        var misses = new List<string>();
        foreach (var id in wanted)
        {
            if (_cache.TryGetValue(id, out var entry) && entry.ExpiresAt > now)
            {
                result[id] = entry.Profile;
            }
            else
            {
                misses.Add(id);
            }
        }

        if (misses.Count == 0)
        {
            return result;
        }

        var stored = await profiles.FindManyByUserAsync(misses, ct).ConfigureAwait(false);
        foreach (var id in misses)
        {
            var profile = stored.TryGetValue(id, out var found)
                ? found
                : await AssignAsync(id, ct).ConfigureAwait(false);

            Remember(id, profile, now);
            result[id] = profile;
        }

        return result;
    }

    /// <summary>Forgets what this instance knows about an account, after its own edit.</summary>
    public void Invalidate(string userId) => _cache.TryRemove(userId, out _);

    private async Task<PublicProfile> AssignAsync(string userId, CancellationToken ct)
    {
        // Member-since is the profile's creation date when the account has saved one; otherwise
        // it is now. The sign-in pool's own creation date is not reachable from here.
        var existingAccount = await accounts.FindAsync(userId, ct).ConfigureAwait(false);
        var memberSince = existingAccount?.CreatedAt ?? clock.UtcNow;

        for (var attempt = 1; attempt <= MaxClaimAttempts; attempt++)
        {
            var profile = PublicProfile.Create(userId, PublicHandle.Generate(), memberSince, clock);

            switch (await profiles.CreateAsync(profile, ct).ConfigureAwait(false))
            {
                case PublicProfileWriteResult.Written:
                    LogAssigned(logger);
                    return profile;

                case PublicProfileWriteResult.AlreadyExists:
                    // Another request for the same account won the race; use what it wrote.
                    return await profiles.FindByUserAsync(userId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("A public profile reported as existing could not be read back.");

                case PublicProfileWriteResult.HandleTaken:
                    break;
            }
        }

        throw DomainException.For("handle.unavailable", "A public handle could not be assigned. Please try again.");
    }

    private void Remember(string userId, PublicProfile profile, DateTimeOffset now)
    {
        if (_cache.Count >= MaxCachedEntries)
        {
            _cache.Clear();
        }

        _cache[userId] = new CacheEntry(profile, now + CacheLifetime);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Assigned a public handle to an account that had none.")]
    private static partial void LogAssigned(ILogger logger);

    private sealed record CacheEntry(PublicProfile Profile, DateTimeOffset ExpiresAt);
}
