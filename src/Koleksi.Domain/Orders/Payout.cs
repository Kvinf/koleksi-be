using Koleksi.Domain.Common;

namespace Koleksi.Domain.Orders;

/// <summary>Where a payout is in its life.</summary>
public enum PayoutStatus
{
    /// <summary>Owed to the seller and not yet sent (or sent and refused, and waiting to be tried again).</summary>
    Pending = 0,

    /// <summary>The payment provider accepted it.</summary>
    Paid = 1,

    /// <summary>Retries ran out. A person has to look; the money is still owed.</summary>
    Failed = 2,
}

/// <summary>
/// What the seller is owed once an order's escrow is released, kept as its own record so the debt survives
/// whatever happens to the transfer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Release and payout are two steps on purpose.</b> Releasing escrow is a decision (the buyer confirmed,
/// or two days passed) and is recorded at once. Sending the money is a call to an outside provider that can
/// be down, slow, or refuse. If the two were one step, a provider outage would either block the release or
/// lose the debt. As separate steps, release always sticks, a <see cref="Payout"/> row is written with it,
/// and a sweep keeps trying until the provider accepts.
/// </para>
/// <para>
/// One payout per order, enforced by the storage key, so a repeated release can never owe the seller twice.
/// The amount is the order's seller payout (items less the fee that applied when the order was created),
/// copied here so a later change to the fee cannot change what was owed.
/// </para>
/// </remarks>
public sealed class Payout
{
    /// <summary>Attempts after which a payout stops retrying and needs a person.</summary>
    public const int MaxAttempts = 8;

    private Payout(string orderId, string orderNumber, string sellerId, Money amount, Money fee, DateTimeOffset createdAt)
    {
        OrderId = orderId;
        OrderNumber = orderNumber;
        SellerId = sellerId;
        Amount = amount;
        Fee = fee;
        CreatedAt = createdAt;
        NextAttemptAt = createdAt;
        Status = PayoutStatus.Pending;
    }

    public string OrderId { get; }
    public string OrderNumber { get; }
    public string SellerId { get; }

    /// <summary>What is sent to the seller: the items total less the platform fee. Shipping is not part of it.</summary>
    public Money Amount { get; }

    /// <summary>The platform's fee, kept for the record.</summary>
    public Money Fee { get; }

    public PayoutStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>The provider's own reference for the transfer, once it accepted one.</summary>
    public string? ProviderReference { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>The last reason the provider (or this system) gave for not sending. Never a secret.</summary>
    public string? LastError { get; private set; }

    /// <summary>Optimistic-concurrency token.</summary>
    public long Version { get; private set; }

    /// <summary>The payout a released order owes its seller.</summary>
    public static Payout For(Order order, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(clock);

        if (order.EscrowState != EscrowState.Released)
        {
            throw DomainException.For("payout.order_not_released", "Only a released order produces a payout.");
        }

        return new Payout(order.Id, order.OrderNumber, order.SellerId, order.SellerPayout, order.PlatformFee, clock.UtcNow);
    }

    /// <summary>
    /// What the seller is owed out of the part of a refunded order the refund did not cover, when staff decide to pay it to them.
    /// The amount is staff's decision, bounded by <see cref="Refund"/>, and copied here like any payout.
    /// </summary>
    public static Payout ForRemainder(Order order, Money amount, Money fee, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(clock);

        if (order.EscrowState != EscrowState.Refunded || order.Dispute is not { Resolution: DisputeResolution.RefundToBuyer })
        {
            throw DomainException.For("payout.order_not_refunded", "Only an order refunded by a dispute ruling can pay out its remainder.");
        }

        return amount.IsZero
            ? throw DomainException.For("payout.amount_invalid", "A payout must be more than zero.")
            : new Payout(order.Id, order.OrderNumber, order.SellerId, amount, fee, clock.UtcNow);
    }

    public static Payout Rehydrate(
        string orderId,
        string orderNumber,
        string sellerId,
        Money amount,
        Money fee,
        PayoutStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset? paidAt,
        string? providerReference,
        int attempts,
        string? lastError,
        long version) =>
        new(orderId, orderNumber, sellerId, amount, fee, createdAt)
        {
            Status = status,
            NextAttemptAt = nextAttemptAt,
            PaidAt = paidAt,
            ProviderReference = providerReference,
            Attempts = attempts,
            LastError = lastError,
            Version = version,
        };

    /// <summary>How long one attempt owns the payout before another sender may take over (a crashed sender's claim lapses).</summary>
    public static readonly TimeSpan AttemptLease = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Claims the payout for one attempt by pushing its next-attempt time out by <see cref="AttemptLease"/>.
    /// The caller saves this with a version check before calling the provider, so only one sender proceeds.
    /// </summary>
    public void BeginAttempt(IClock clock)
    {
        if (Status != PayoutStatus.Pending)
        {
            return;
        }

        NextAttemptAt = clock.UtcNow + AttemptLease;
        Version++;
    }

    /// <summary>
    /// Makes a waiting payout due now. Used when what it was waiting for has been fixed (the seller added
    /// a bank account), so the money goes out at once rather than after the slow end of the backoff.
    /// </summary>
    public void RetryNow(IClock clock)
    {
        if (Status != PayoutStatus.Pending)
        {
            return;
        }

        NextAttemptAt = clock.UtcNow;
        Version++;
    }

    /// <summary>The provider accepted the transfer.</summary>
    public void MarkPaid(string providerReference, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReference);

        if (Status == PayoutStatus.Paid)
        {
            return;
        }

        Status = PayoutStatus.Paid;
        ProviderReference = providerReference;
        PaidAt = clock.UtcNow;
        LastError = null;
        Version++;
    }

    /// <summary>
    /// The transfer was not sent. A later attempt is scheduled with a growing wait, and after
    /// <see cref="MaxAttempts"/> the payout is marked <see cref="PayoutStatus.Failed"/> for a person.
    /// </summary>
    /// <param name="retryable"><c>false</c> for a refusal retrying cannot fix (no bank account on file).</param>
    public void RecordFailure(string reason, bool retryable, IClock clock)
    {
        if (Status == PayoutStatus.Paid)
        {
            return;
        }

        Attempts++;
        LastError = reason.Length > 300 ? reason[..300] : reason;
        Version++;

        // A refusal retrying cannot fix still waits and tries again: the seller adding a bank account
        // fixes it, so it is retried at the slow end of the schedule rather than given up on.
        if (Attempts >= MaxAttempts && retryable)
        {
            Status = PayoutStatus.Failed;
            return;
        }

        NextAttemptAt = clock.UtcNow + BackoffAfter(Attempts, retryable);
    }

    /// <summary>5 minutes, then 15, then an hour, 6 hours and a day; a refusal that needs the seller waits an hour at least.</summary>
    internal static TimeSpan BackoffAfter(int attempts, bool retryable)
    {
        var wait = attempts switch
        {
            <= 1 => TimeSpan.FromMinutes(5),
            2 => TimeSpan.FromMinutes(15),
            3 => TimeSpan.FromHours(1),
            4 => TimeSpan.FromHours(6),
            _ => TimeSpan.FromDays(1),
        };

        return retryable ? wait : (wait < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait);
    }
}
