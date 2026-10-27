using Koleksi.Application.Abstractions;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Koleksi.Infrastructure.Payments;

/// <summary>
/// Sends a released order's money to the seller through the payment provider, with the same merchant
/// account and credentials as <see cref="PaymentGateway"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The provider call is not implemented yet.</b> Like <c>PaymentGateway.CreateSessionAsync</c>, it needs
/// the provider's live disbursement contract and sandbox credentials, which are not part of this pass.
/// Until then every attempt is answered "not configured" and is <b>not</b> retryable (so it never burns the
/// payout's attempts into <see cref="PayoutStatus.Failed"/>): the payout stays pending, visible to the seller
/// as "being prepared", and goes out on the first sweep after the call below is written. Nothing is lost:
/// the release and the debt are already recorded.
/// </para>
/// <para>
/// When it is written it must: POST to <c>{Payment:BaseUrl}</c> with the merchant <c>ClientId</c> and a
/// request signature made from the secret in <c>Payment:SecretArn</c> (read through the same
/// <c>PaymentSecret</c> the webhook uses); send <see cref="Payout.OrderId"/> as the provider's idempotency
/// key so a retried attempt can never pay twice; and answer <see cref="PayoutResult.Refused"/> (never throw)
/// for a refusal, with <c>retryable: false</c> for one only a person can fix.
/// </para>
/// </remarks>
internal sealed partial class GatewayEscrowPayout(PaymentOptions options, ILogger<GatewayEscrowPayout> logger) : IEscrowPayout
{
    public const string NotConfigured = "payout.not_configured";

    public Task<PayoutResult> SendAsync(Payout payout, BankAccount? destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payout);

        LogNotConfigured(logger, payout.OrderNumber, Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : "unset");
        return Task.FromResult(PayoutResult.Refused(NotConfigured, retryable: false));
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Payout for order {OrderNumber} is waiting: the provider disbursement call is not implemented (provider host {Host}). It is sent once it is.")]
    private static partial void LogNotConfigured(ILogger logger, string orderNumber, string host);
}

/// <summary>
/// The delivery tracker for a stage with no courier-tracking provider: it cannot answer, so parcels are
/// released only when the buyer confirms receipt.
/// </summary>
/// <remarks>
/// Courier tracking is not something the payment gateway provides, so it needs its own provider and
/// credentials. Until one is chosen, "two days after delivery" cannot start by itself; the buyer's
/// confirmation (always available from the moment the order is shipped) is the only trigger.
/// </remarks>
internal sealed class NotConfiguredDeliveryTracker : IDeliveryTracker
{
    public bool IsConfigured => false;

    public Task<DeliveryReport> CheckAsync(Order order, CancellationToken ct) => Task.FromResult(DeliveryReport.Unknown);
}
