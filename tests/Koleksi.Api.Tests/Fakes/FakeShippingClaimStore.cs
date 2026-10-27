using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IShippingClaimStore"/> with the real store's rules: a value backs one order, a repeat for the
/// same order passes, and a refused claim writes nothing.
/// </summary>
public sealed class FakeShippingClaimStore : FakeAdapter, IShippingClaimStore
{
    private readonly Dictionary<string, string> _claims = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<ShippingClaimConflict?> TryClaimAsync(
        string orderId, string courierCode, string trackingNumber, IReadOnlyList<string> proofKeys, CancellationToken ct)
    {
        ThrowIfFailing();

        var wanted = new List<(string Key, ShippingClaimConflict OnClash)>
        {
            ($"TRACK#{courierCode}#{trackingNumber}", ShippingClaimConflict.TrackingReused),
        };
        wanted.AddRange(proofKeys.Distinct(StringComparer.Ordinal).Select(key => ($"PROOF#{key}", ShippingClaimConflict.ProofReused)));

        lock (_gate)
        {
            foreach (var (key, onClash) in wanted)
            {
                if (_claims.TryGetValue(key, out var owner) && owner != orderId)
                {
                    return Task.FromResult<ShippingClaimConflict?>(onClash);
                }
            }

            foreach (var (key, _) in wanted)
            {
                _claims[key] = orderId;
            }
        }

        return Task.FromResult<ShippingClaimConflict?>(null);
    }
}
