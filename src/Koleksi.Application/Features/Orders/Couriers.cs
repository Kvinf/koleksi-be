using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Application.Features.Orders;

/// <summary>
/// A flat-rate stand-in for real courier rate-shopping.
/// </summary>
/// <remarks>
/// No rate-shopping integration exists — the same kind of intentional gap
/// <see cref="Koleksi.Application.Abstractions.IPaymentGateway.CreateSessionAsync"/> is for
/// the payment processor. A real integration would quote cost and ETA per shipment (origin,
/// destination, weight); this is a fixed table keyed only on the courier the buyer picked,
/// good enough to let checkout produce a real, payable order while that integration is
/// still a TODO.
/// </remarks>
public static class FlatRateCouriers
{
    private static readonly IReadOnlyDictionary<string, (string ServiceName, long CostRupiah, int EstimatedDays)>
        Table = new Dictionary<string, (string, long, int)>(StringComparer.Ordinal)
        {
            ["jne"] = ("JNE REG", 15_000, 3),
            ["jnt"] = ("J&T Express", 14_000, 3),
            ["sicepat"] = ("SiCepat REG", 13_000, 2),
            ["anteraja"] = ("AnterAja REG", 12_000, 3),
            ["gosend"] = ("GoSend Instant", 20_000, 1),
        };

    public static IReadOnlyCollection<string> Codes => (IReadOnlyCollection<string>)Table.Keys;

    public static CourierSelection? Find(string courierCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(courierCode);

        return Table.TryGetValue(courierCode, out var courier)
            ? new CourierSelection(courierCode, courier.ServiceName, Money.FromRupiah(courier.CostRupiah), courier.EstimatedDays)
            : null;
    }
}
