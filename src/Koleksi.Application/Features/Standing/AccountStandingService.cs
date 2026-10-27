using System.Collections.Concurrent;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Standing;

/// <summary>Reads and writes an account's <see cref="AccountStanding"/>.</summary>
public interface IAccountStandingRepository
{
    /// <summary>The account's record, or <c>null</c> if it has never been warned.</summary>
    Task<AccountStanding?> FindAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Saves the record, conditional on its version: <paramref name="expectedVersion"/> <c>0</c> means
    /// "must not exist yet". A concurrent writer makes this throw <see cref="ConcurrencyConflictException"/>.
    /// </summary>
    Task SaveAsync(AccountStanding standing, long expectedVersion, CancellationToken ct);
}

/// <summary>
/// A short in-process memory of who is banned, so the ban check on every write request is not a
/// database read each time.
/// </summary>
/// <remarks>
/// Per process, so another Lambda instance learns of a new ban within <see cref="Lifetime"/> rather than
/// at once. That window is the cost of not reading storage on every request; a ban recorded in this
/// process takes effect here immediately (see <see cref="Set"/>).
/// </remarks>
public sealed class BanStatusCache(IClock clock)
{
    /// <summary>How long a cached answer is believed.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private const int MaxEntries = 20_000;

    private readonly ConcurrentDictionary<string, (bool Banned, DateTimeOffset Until)> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string userId, out bool banned)
    {
        if (_entries.TryGetValue(userId, out var entry) && entry.Until > clock.UtcNow)
        {
            banned = entry.Banned;
            return true;
        }

        banned = false;
        return false;
    }

    public void Set(string userId, bool banned)
    {
        if (_entries.Count >= MaxEntries)
        {
            // A flood of distinct callers must not grow memory without bound: start over rather than evict one by one.
            _entries.Clear();
        }

        _entries[userId] = (banned, clock.UtcNow + Lifetime);
    }
}

/// <summary>
/// Counts broken commitments against accounts, bans the third, and answers "may this account act?".
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly once per deal.</b> A violation is keyed by the deal it belongs to (the auction or the
/// offer), and the record refuses a deal it has already counted, so an order that is cancelled by the
/// sweep and also found by the auction sweep is one warning, not two.
/// </para>
/// <para>
/// <b>What a ban does.</b> It is enforced where requests enter (see <c>BannedAccountMiddleware</c>):
/// the account can still read, sign out and finish an order that is already paid, but cannot start
/// anything new. Its listings are taken out of browse the moment it is banned, and checkout refuses
/// them, so nobody can buy from a banned seller.
/// </para>
/// </remarks>
public sealed partial class AccountStandingService(
    IAccountStandingRepository repository,
    IListingRepository listings,
    IListingSearchIndex searchIndex,
    BanStatusCache banCache,
    IClock clock,
    ILogger<AccountStandingService> logger)
{
    private const int MaxAttempts = 4;

    /// <summary>How many pages of a banned seller's listings are taken out of browse (50 each).</summary>
    private const int MaxListingPages = 10;

    /// <summary>The account's standing as shown to its owner.</summary>
    public async Task<StandingView> GetAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var standing = await repository.FindAsync(userId, ct).ConfigureAwait(false);
        return ViewOf(standing, clock.UtcNow);
    }

    /// <summary>Whether the account is banned (cached for <see cref="BanStatusCache.Lifetime"/>).</summary>
    public async Task<bool> IsBannedAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (banCache.TryGet(userId, out var cached))
        {
            return cached;
        }

        var banned = (await repository.FindAsync(userId, ct).ConfigureAwait(false))?.IsBanned == true;
        banCache.Set(userId, banned);
        return banned;
    }

    /// <summary>Refuses with <c>account.banned</c> when the account is banned.</summary>
    public async Task EnsureNotBannedAsync(string userId, CancellationToken ct)
    {
        if (await IsBannedAsync(userId, ct).ConfigureAwait(false))
        {
            throw DomainException.For(
                "account.banned",
                "This account has been banned after repeated broken commitments. Contact support.");
        }
    }

    /// <summary>
    /// Counts a broken commitment against <paramref name="userId"/>. Safe to call twice for the same deal.
    /// </summary>
    public async Task<StandingOutcome> RecordViolationAsync(
        string userId, ViolationType type, string reference, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var standing = await repository.FindAsync(userId, ct).ConfigureAwait(false);
            var expectedVersion = standing?.Version ?? 0;
            standing ??= AccountStanding.Clean(userId);

            var outcome = standing.Record(type, reference, clock);
            if (outcome is StandingOutcome.Duplicate or StandingOutcome.AlreadyBanned)
            {
                return outcome;
            }

            try
            {
                await repository.SaveAsync(standing, expectedVersion, ct).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                // Another violation landed at the same moment. Read it again and add this one on top.
                continue;
            }

            banCache.Set(userId, standing.IsBanned);
            LogViolation(logger, userId, type.ToString(), reference, outcome.ToString(), standing.WarningsAt(clock.UtcNow));

            if (outcome == StandingOutcome.Banned)
            {
                await TakeListingsOffShelfAsync(userId, ct).ConfigureAwait(false);
            }

            return outcome;
        }

        throw new ConcurrencyConflictException($"Standing of '{userId}' could not be updated.");
    }

    internal static StandingView ViewOf(AccountStanding? standing, DateTimeOffset now) =>
        standing is null
            ? new StandingView(0, AccountStanding.WarningsToBan, Banned: false, NextReductionAt: null, [])
            : new StandingView(
                standing.WarningsAt(now),
                AccountStanding.WarningsToBan,
                standing.IsBanned,
                standing.NextReductionAt(now),
                standing.Recent.Select(v => new ViolationView(v.Type.ToString(), v.At)).ToList());

    /// <summary>
    /// A banned seller's listings leave browse. Best effort and bounded: the ban itself is already saved,
    /// and checkout refuses a banned seller regardless of what browse still shows.
    /// </summary>
    private async Task TakeListingsOffShelfAsync(string userId, CancellationToken ct)
    {
        try
        {
            string? cursor = null;
            for (var page = 0; page < MaxListingPages; page++)
            {
                var batch = await listings.ListBySellerAsync(userId, status: null, cursor, limit: 50, ct).ConfigureAwait(false);
                foreach (var listing in batch.Items.Where(l => l.Status == ListingStatus.Active))
                {
                    await searchIndex.RemoveAsync(listing.Id, ct).ConfigureAwait(false);
                }

                cursor = batch.NextCursor;
                if (cursor is null)
                {
                    break;
                }
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogShelfFailed(logger, userId, failure);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Account {UserId} violation {Type} on {Reference}: {Outcome}, {Warnings} warning(s) now.")]
    private static partial void LogViolation(
        ILogger logger, string userId, string type, string reference, string outcome, int warnings);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Account {UserId} was banned but its listings could not all be taken out of browse; checkout still refuses them.")]
    private static partial void LogShelfFailed(ILogger logger, string userId, Exception exception);
}

/// <summary>An account's standing as its owner sees it.</summary>
/// <param name="Warnings">Warnings carried right now, after forgiveness.</param>
/// <param name="MaxWarnings">The count that bans.</param>
/// <param name="NextReductionAt">When one warning is forgiven next; <c>null</c> with none to forgive or when banned.</param>
public sealed record StandingView(
    int Warnings,
    int MaxWarnings,
    bool Banned,
    DateTimeOffset? NextReductionAt,
    IReadOnlyList<ViolationView> Recent);

public sealed record ViolationView(string Type, DateTimeOffset At);
