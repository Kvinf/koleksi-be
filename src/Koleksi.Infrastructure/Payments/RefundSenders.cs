using Koleksi.Application.Abstractions;
using Koleksi.Domain.Orders;

namespace Koleksi.Infrastructure.Payments;

/// <summary>
/// The refund sender every stage uses today: there is no payment gateway to send through, so it records that and moves no money.
/// </summary>
/// <remarks>
/// A real gateway adapter replaces this class. It will need the gateway's refund contract and the same discipline the payout path has
/// (ledger first, transfer second, the refund's id as the provider's idempotency key); none of that exists yet.
/// </remarks>
internal sealed class PendingGatewayRefundSender : IRefundSender
{
    public Task<RefundStatus> SendAsync(Refund refund, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refund);
        return Task.FromResult(RefundStatus.PendingGateway);
    }
}

/// <summary>
/// The development stand-in: accepts the refund and says so, and moves no money either. Registered only when
/// <c>KoleksiAdmin:Simulators:Enabled</c> is on, which <see cref="SimulatorOptions"/> refuses on any stage but dev and a developer's machine.
/// </summary>
internal sealed class SimulatedRefundSender : IRefundSender
{
    public Task<RefundStatus> SendAsync(Refund refund, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refund);
        return Task.FromResult(RefundStatus.Simulated);
    }
}
