using Koleksi.Domain.Common;

namespace Koleksi.Application.Abstractions;

/// <summary>
/// Keeps a record of money the gateway says it took that the marketplace could not apply to an
/// order, so a person can refund it.
/// </summary>
/// <remarks>
/// <para>
/// The payment webhook is the only evidence that a buyer paid. When it cannot be applied (the order
/// was cancelled before the payment landed, or the amount does not match) the handler must still answer
/// <c>200</c>: the gateway delivers at least once and a retry of the same notification fails the same
/// way for ever. Dropping it silently would leave a buyer charged for nothing and nobody who knows,
/// so the facts are written down here and logged at error level.
/// </para>
/// <para>
/// Recording is idempotent on (order number, processor reference): a redelivery of the same
/// notification writes nothing new. Rows are never deleted by the application.
/// </para>
/// </remarks>
public interface IPaymentOrphanStore
{
    /// <summary>Records one orphaned payment. A repeat of the same payment is a no-op.</summary>
    Task RecordAsync(PaymentOrphan orphan, CancellationToken ct);
}

/// <param name="OrderNumber">The reference the gateway echoed back (the order number).</param>
/// <param name="OrderId">The order's id, when it was found.</param>
/// <param name="Amount">What the gateway reports it settled.</param>
/// <param name="ProcessorReference">The processor's own transaction id, for refunding it.</param>
/// <param name="EventId">The notification's own id.</param>
/// <param name="Reason">Why it could not be applied, e.g. <c>order_cancelled</c>, <c>amount_mismatch</c>, <c>listing_unavailable</c>.</param>
/// <param name="ReceivedAt">When the notification was processed.</param>
public sealed record PaymentOrphan(
    string OrderNumber,
    string? OrderId,
    Money Amount,
    string ProcessorReference,
    string EventId,
    string Reason,
    DateTimeOffset ReceivedAt);

/// <summary>
/// The payment gateway's webhook could not be verified because the signing key could not be read
/// (Secrets Manager unreachable, empty secret). Distinct from a forged signature: nothing is wrong
/// with the notification, so the answer is <c>503</c> and the gateway retries, not <c>401</c> or a bare <c>500</c>.
/// </summary>
public sealed class PaymentVerificationUnavailableException(string message, Exception inner)
    : Exception(message, inner);
