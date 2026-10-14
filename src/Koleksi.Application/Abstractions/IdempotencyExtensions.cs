using System.Text.Json;

namespace Koleksi.Application.Abstractions;

/// <summary>What a second request does when the first one carrying its key has not finished.</summary>
public enum InFlightPolicy
{
    /// <summary>
    /// Run the work again. For cheap, harmless endpoints (a profile edit, an OTP request) where
    /// stranding a reader on a "try again" screen is worse than the small chance of a duplicate.
    /// </summary>
    RunAgain = 0,

    /// <summary>
    /// Refuse with <see cref="RequestInProgressException"/>. For endpoints that move money or
    /// change who owns what, where running twice creates two orders or two releases.
    /// </summary>
    Reject = 1,
}

/// <summary>
/// Raised when a request's idempotency key is already claimed by a request that has not finished.
/// </summary>
/// <remarks>
/// The API answers <c>409</c> <c>request.in_progress</c> with a <c>Retry-After</c>. The caller's
/// retry then replays the first request's result once it is recorded.
/// </remarks>
public sealed class RequestInProgressException()
    : Exception("Another request with the same Idempotency-Key is still being processed.");

/// <summary>
/// The one call-site pattern every double-submit-sensitive handler uses on top of
/// <see cref="IIdempotencyStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IIdempotencyStore"/> stays a thin primitive (claim, record, replay, release) on
/// purpose — this is the policy layered on it, not a second copy of it. Claim the key; if it
/// was already claimed and a result was already recorded, return that result instead of
/// running <paramref name="work"/> again; otherwise run the work once and record it.
/// </para>
/// <para>
/// <b>The no-key case is not an error.</b> A caller that sends no <c>Idempotency-Key</c>
/// simply is not protected — that is the endpoint's choice by whether it reads the header
/// at all, not something this helper enforces from the inside.
/// </para>
/// <para>
/// <b>The claimed-but-not-yet-recorded case</b> — a second request arriving while the first
/// is still mid-flight — is the caller's choice through <see cref="InFlightPolicy"/>. Money-moving
/// endpoints pass <see cref="InFlightPolicy.Reject"/>: three parallel checkouts with one key
/// must not make three orders. The rest keep <see cref="InFlightPolicy.RunAgain"/>.
/// </para>
/// <para>
/// <b>A failed request does not hold its key.</b> When the work throws, the claim is released so
/// the client's retry runs instead of being told the request is in progress. A request that dies
/// without getting that far (a timed-out Lambda) is covered by <see cref="InFlightLease"/>: the
/// store treats a claim with no result that old as abandoned.
/// </para>
/// </remarks>
public static class IdempotencyExtensions
{
    /// <summary>
    /// How long a claim with no recorded result is believed to be in flight.
    /// </summary>
    /// <remarks>
    /// Longer than the API Lambda's timeout (25 s), so a request that is still legitimately
    /// running is never taken over, and short enough that one that crashed does not lock its key
    /// for the whole retention window (up to 10 minutes on checkout).
    /// </remarks>
    public static readonly TimeSpan InFlightLease = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<T> RunOnceAsync<T>(
        this IIdempotencyStore store,
        string scope,
        string? idempotencyKey,
        TimeSpan retention,
        Func<Task<T>> work,
        CancellationToken ct,
        InFlightPolicy inFlight = InFlightPolicy.RunAgain)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(work);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return await work().ConfigureAwait(false);
        }

        if (!await store.TryClaimAsync(scope, idempotencyKey, retention, ct).ConfigureAwait(false))
        {
            var recorded = await store.GetRecordedResultAsync(scope, idempotencyKey, ct).ConfigureAwait(false);
            if (recorded is not null)
            {
                return JsonSerializer.Deserialize<T>(recorded, SerializerOptions)!;
            }

            if (inFlight == InFlightPolicy.Reject)
            {
                throw new RequestInProgressException();
            }
        }

        T result;
        try
        {
            result = await work().ConfigureAwait(false);
        }
        catch
        {
            await ReleaseQuietlyAsync(store, scope, idempotencyKey).ConfigureAwait(false);
            throw;
        }

        await store
            .RecordResultAsync(scope, idempotencyKey, JsonSerializer.Serialize(result, SerializerOptions), ct)
            .ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Best effort, and deliberately not tied to the request's cancellation token: the common
    /// reason the work threw is that the client went away, and the release must still happen.
    /// </summary>
    /// <remarks>
    /// If it fails too, the lease expires the claim on its own, so swallowing the failure here
    /// costs a short wait and never a stuck key. The original exception is what the caller needs.
    /// </remarks>
    private static async Task ReleaseQuietlyAsync(IIdempotencyStore store, string scope, string key)
    {
        try
        {
            await store.ReleaseClaimAsync(scope, key, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // See remarks: the lease is the backstop.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
