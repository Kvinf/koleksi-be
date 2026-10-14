using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;

namespace Koleksi.Api.Development;

/// <summary>
/// Stands in for the courier-tracking provider so the whole escrow flow (shipped, delivered, two days, released,
/// paid out) can be run by hand. Registered only when `Koleksi:Simulators:Enabled` is on, which the host refuses
/// anywhere but a developer's machine or the dev stage.
/// </summary>
/// <remarks>
/// By default it reports nothing: the courier is moved by hand from the simulator page
/// (<c>SimulatorEndpoints</c>), so a parcel arrives when the tester says. Set
/// <c>Koleksi:Development:SimulatedDeliveryMinutes</c> to a number of minutes (0 or more) to have the sweep report
/// a parcel delivered that long after it shipped instead. It does not call anyone.
/// </remarks>
internal sealed class LocalDeliveryTracker(IClock clock, IConfiguration configuration) : IDeliveryTracker
{
    public bool IsConfigured => true;

    /// <summary>Only asked about parcels when a delivery delay is configured; otherwise the courier is moved by hand.</summary>
    public bool CanPoll => configuration.GetValue("Koleksi:Development:SimulatedDeliveryMinutes", -1) >= 0;

    public Task<DeliveryReport> CheckAsync(Order order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);

        var minutes = configuration.GetValue("Koleksi:Development:SimulatedDeliveryMinutes", -1);
        if (minutes < 0)
        {
            return Task.FromResult(DeliveryReport.Unknown);
        }

        var shippedAt = order.ShippedAt ?? clock.UtcNow;

        return Task.FromResult(
            clock.UtcNow >= shippedAt + TimeSpan.FromMinutes(Math.Max(0, minutes))
                ? new DeliveryReport(DeliveryState.Delivered, clock.UtcNow)
                : new DeliveryReport(DeliveryState.InTransit));
    }
}

/// <summary>
/// Accepts every payout instantly so the seller's payout list can be seen going from pending to paid. Moves no
/// money. Registered only with the simulators (see above).
/// </summary>
internal sealed class LocalEscrowPayout : IEscrowPayout
{
    public Task<PayoutResult> SendAsync(Payout payout, BankAccount? destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payout);

        return Task.FromResult(PayoutResult.Sent($"local-{payout.OrderId}"));
    }
}
