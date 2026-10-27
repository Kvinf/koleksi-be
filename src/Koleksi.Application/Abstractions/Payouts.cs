using Koleksi.Application.Common;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;

namespace Koleksi.Application.Abstractions;

/// <summary>Stores the payouts owed to sellers.</summary>
public interface IPayoutRepository
{
    /// <summary>
    /// Writes the payout unless one already exists for the order (one payout per order, whatever happens twice).
    /// </summary>
    /// <returns><c>true</c> when this call created it, <c>false</c> when it was already there.</returns>
    Task<bool> AddIfAbsentAsync(Payout payout, CancellationToken ct);

    Task<Payout?> FindByOrderAsync(string orderId, CancellationToken ct);

    /// <summary>Saves the payout, conditional on the version that was read.</summary>
    Task UpdateAsync(Payout payout, long expectedVersion, CancellationToken ct);

    /// <summary>Pending payouts whose next attempt time has come, longest-waiting first. Sparse index, never a scan.</summary>
    Task<IReadOnlyList<Payout>> ListDueAsync(DateTimeOffset asOf, int limit, CancellationToken ct);

    /// <summary>A seller's payouts, newest first.</summary>
    Task<Page<Payout>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct);
}

/// <summary>
/// Sends a seller their money. The real adapter talks to the payment provider with the same merchant
/// credentials as <see cref="IPaymentGateway"/>.
/// </summary>
public interface IEscrowPayout
{
    /// <summary>
    /// Attempts the transfer. Never throws for a refusal: the outcome says whether it was accepted and,
    /// if not, whether trying again could help.
    /// </summary>
    /// <param name="destination">The seller's bank account, or <c>null</c> when they have none on file.</param>
    Task<PayoutResult> SendAsync(Payout payout, BankAccount? destination, CancellationToken ct);
}

/// <param name="Accepted">The provider took the transfer.</param>
/// <param name="Reference">The provider's reference, when accepted.</param>
/// <param name="Error">Why it was not sent, when not accepted. Never a credential or a payload.</param>
/// <param name="Retryable"><c>false</c> when only a person (or the seller) can fix it.</param>
public sealed record PayoutResult(bool Accepted, string? Reference, string? Error, bool Retryable)
{
    public static PayoutResult Sent(string reference) => new(true, reference, null, false);

    public static PayoutResult Refused(string error, bool retryable) => new(false, null, error, retryable);
}

/// <summary>
/// Asks the courier whether a parcel has been delivered. The real adapter needs a courier-tracking
/// provider; until one is chosen the system relies on the buyer confirming receipt.
/// </summary>
public interface IDeliveryTracker
{
    /// <summary>
    /// Whether delivery will be learned at all: by asking the courier (<see cref="CanPoll"/>) or by the provider
    /// pushing it (<see cref="IDeliveryWebhookProvider"/>). <c>false</c> for the "no provider" stand-in, which is
    /// what makes the order page say only the buyer's confirmation releases the money.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Whether the sweep should ask this tracker about shipped parcels. A provider that only pushes returns
    /// <c>false</c>, so the sweep does not write a "checked, nothing yet" mark on every shipped order every half hour.
    /// </summary>
    bool CanPoll => IsConfigured;

    Task<DeliveryReport> CheckAsync(Order order, CancellationToken ct);

    /// <summary>
    /// Tells the provider a parcel has shipped so it starts following it (most providers must be given the waybill
    /// first). Called once, best effort, right after the seller marks the order shipped; a failure is logged and
    /// never undoes the shipment. The provider must be given <see cref="Order.OrderNumber"/> as its reference for
    /// the parcel, because that is how a pushed notification finds the order again.
    /// </summary>
    Task RegisterShipmentAsync(Order order, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// One courier-tracking provider's webhook: checks it really came from them and turns it into a
/// <see cref="DeliveryNotification"/>. A new provider is one class implementing this (and, if it can also be asked,
/// an <see cref="IDeliveryTracker"/>), registered in <c>InfrastructureServiceCollectionExtensions</c>.
/// </summary>
public interface IDeliveryWebhookProvider
{
    /// <summary>The name in the address, <c>/api/shipping/webhook/{Name}</c>. Lower case, letters and digits.</summary>
    string Name { get; }

    /// <summary>
    /// Verifies the body's signature (or whatever the provider authenticates with) over the raw, unparsed body and
    /// reads it. Returns <c>null</c> when it cannot be trusted or understood, never a half-parsed value.
    /// </summary>
    /// <exception cref="DeliveryVerificationUnavailableException">The key could not be read; the call may be genuine and should be retried.</exception>
    Task<DeliveryNotification?> VerifyAndParseAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

/// <summary>What a provider said happened to one parcel, in our terms.</summary>
/// <param name="EventId">The provider's own id for this event; the same event sent twice is applied once.</param>
/// <param name="OrderNumber">Our <c>KLK-…</c> number, which we gave the provider as its reference.</param>
public sealed record DeliveryNotification(string EventId, string OrderNumber, DeliveryState State);

/// <summary>The signing key could not be read, so a webhook could be neither accepted nor refused.</summary>
public sealed class DeliveryVerificationUnavailableException : Exception
{
    public DeliveryVerificationUnavailableException() { }

    public DeliveryVerificationUnavailableException(string message) : base(message) { }

    public DeliveryVerificationUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>What the courier said about one parcel.</summary>
public enum DeliveryState
{
    /// <summary>No answer (provider down, unknown waybill, or no provider at all). Ask again later.</summary>
    Unknown = 0,

    /// <summary>The parcel is on its way.</summary>
    InTransit = 1,

    /// <summary>The courier reports it delivered.</summary>
    Delivered = 2,
}

/// <param name="DeliveredAt">The courier's own delivery time when it gave one; the check time is used otherwise.</param>
public sealed record DeliveryReport(DeliveryState State, DateTimeOffset? DeliveredAt = null)
{
    public static DeliveryReport Unknown { get; } = new(DeliveryState.Unknown);
}
