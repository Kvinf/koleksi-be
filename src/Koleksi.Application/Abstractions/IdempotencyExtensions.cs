using System.Text.Json;

namespace Koleksi.Application.Abstractions;

/// <summary>
/// The one call-site pattern every double-submit-sensitive handler uses on top of
/// <see cref="IIdempotencyStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IIdempotencyStore"/> stays a thin three-call primitive (claim, record,
/// replay) on purpose — this is the policy layered on it, not a second copy of it. Claim
/// the key; if it was already claimed and a result was already recorded, return that result
/// instead of running <paramref name="work"/> again; otherwise run the work once and record
/// it.
/// </para>
/// <para>
/// <b>The no-key case is not an error.</b> A caller that sends no <c>Idempotency-Key</c>
/// simply is not protected — that is the endpoint's choice by whether it reads the header
/// at all, not something this helper enforces from the inside.
/// </para>
/// <para>
/// <b>The claimed-but-not-yet-recorded case</b> — a second request arriving while the first
/// is still mid-flight, or after the first crashed before recording — falls through and
/// runs the work again rather than refusing. For a bucket this small and this fast (a
/// single Cognito call), that race window is rare, and stranding a reader on a "try again"
/// screen for what looked to them like one click is worse than the small chance of a
/// duplicate that this trades for it.
/// </para>
/// </remarks>
public static class IdempotencyExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<T> RunOnceAsync<T>(
        this IIdempotencyStore store,
        string scope,
        string? idempotencyKey,
        TimeSpan retention,
        Func<Task<T>> work,
        CancellationToken ct)
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
        }

        var result = await work().ConfigureAwait(false);

        await store
            .RecordResultAsync(scope, idempotencyKey, JsonSerializer.Serialize(result, SerializerOptions), ct)
            .ConfigureAwait(false);

        return result;
    }
}
