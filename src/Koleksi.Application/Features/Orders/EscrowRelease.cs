using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Orders;

/// <summary>
/// Releases an order's escrow and makes sure the seller is owed (and, as soon as possible, paid) for it.
/// </summary>
/// <remarks>
/// <para>
/// Every way escrow ends in the seller's favour goes through here: the buyer confirming receipt, and the
/// timed release two days after the courier reports delivery. The order is released first and the payout
/// is a separate, retried step (see <see cref="Payout"/>), so a payment-provider outage never blocks a
/// release and never loses the debt.
/// </para>
/// <para>
/// <b>A release cannot exist without its payout.</b> The released order and the payout row are written in
/// one transaction (<see cref="IOrderRepository.UpdateWithPayoutAsync"/>), so there is no state where the
/// escrow was released and the seller is not owed.
/// </para>
/// </remarks>
public sealed class EscrowReleaseService(
    IOrderRepository orders,
    PayoutSender sender,
    IClock clock)
{
    /// <summary>
    /// Releases <paramref name="order"/> (the domain decides whether it may be) and starts the payout.
    /// </summary>
    /// <exception cref="DomainException">The order cannot be released (not delivered, disputed, or the window is still open).</exception>
    /// <exception cref="ConcurrencyConflictException">The order changed since it was read.</exception>
    public async Task ReleaseAsync(Order order, bool confirmedByBuyer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);

        // Already released (a repeat of the same confirmation): nothing to write, nothing owed twice.
        if (order.EscrowState == EscrowState.Released)
        {
            return;
        }

        var expectedVersion = order.Version;
        order.Release(confirmedByBuyer, clock);

        var payout = Payout.For(order, clock);
        await orders.UpdateWithPayoutAsync(order, expectedVersion, payout, ct).ConfigureAwait(false);

        // Sending is a separate step that may fail without undoing the release; the payout sweep retries it.
        await sender.TrySendAsync(payout, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Sends one pending payout through the payment provider, once at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never sends twice.</b> An attempt first claims the payout by moving its next-attempt time forward,
/// with a version-checked write: of two senders (the release itself, the sweep) only one gets past that,
/// and the other skips. The provider call then carries the order's id as its idempotency key, so even a
/// claim that outlives a crashed sender and is retried cannot make the provider pay twice.
/// </para>
/// <para>
/// A seller with no bank account on file is not sent to: the payout waits (an hour at a time) and is sent
/// the moment they add one. The reason is kept on the payout so the dashboard can say so.
/// </para>
/// </remarks>
public sealed partial class PayoutSender(
    IPayoutRepository payouts,
    IProfileRepository profiles,
    IEscrowPayout provider,
    IClock clock,
    ILogger<PayoutSender> logger)
{
    public const string MissingBankAccount = "bank_account_missing";

    /// <summary>
    /// A seller has just saved a bank account: sends the payouts that were waiting for one, now. Best effort
    /// and bounded (one page): saving the account has already succeeded, and the sweep picks up anything this
    /// misses, so a failure here is logged and never reaches the seller.
    /// </summary>
    public async Task RetryWaitingForBankAccountAsync(string sellerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        try
        {
            var page = await payouts.ListForSellerAsync(sellerId, cursor: null, limit: 20, ct).ConfigureAwait(false);
            foreach (var payout in page.Items.Where(p =>
                p.Status == PayoutStatus.Pending
                && string.Equals(p.LastError, MissingBankAccount, StringComparison.Ordinal)))
            {
                var expectedVersion = payout.Version;
                payout.RetryNow(clock);
                try
                {
                    await payouts.UpdateAsync(payout, expectedVersion, ct).ConfigureAwait(false);
                }
                catch (ConcurrencyConflictException)
                {
                    continue;
                }

                await TrySendAsync(payout, ct).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogRetryFailed(logger, failure);
        }
    }

    /// <summary>Tries to send <paramref name="payout"/>. Returns whether the provider accepted it in this call.</summary>
    public async Task<bool> TrySendAsync(Payout payout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payout);

        if (payout.Status != PayoutStatus.Pending)
        {
            return payout.Status == PayoutStatus.Paid;
        }

        var profile = await profiles.FindAsync(payout.SellerId, ct).ConfigureAwait(false);
        var destination = profile?.BankAccount;

        // Claim the attempt before touching the provider, so a concurrent sender backs off.
        var expectedVersion = payout.Version;
        payout.BeginAttempt(clock);
        try
        {
            await payouts.UpdateAsync(payout, expectedVersion, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            return false;
        }

        PayoutResult result;
        if (destination is null)
        {
            result = PayoutResult.Refused(MissingBankAccount, retryable: false);
        }
        else
        {
            try
            {
                result = await provider.SendAsync(payout, destination, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                LogProviderFailed(logger, payout.OrderNumber, failure);
                result = PayoutResult.Refused("provider_error", retryable: true);
            }
        }

        var claimedVersion = payout.Version;
        if (result.Accepted)
        {
            payout.MarkPaid(result.Reference ?? payout.OrderId, clock);
        }
        else
        {
            payout.RecordFailure(result.Error ?? "refused", result.Retryable, clock);
        }

        try
        {
            await payouts.UpdateAsync(payout, claimedVersion, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            // Someone moved it on while the provider was being called. If the provider accepted, the
            // idempotency key means the next attempt cannot pay twice; log it so a person can see.
            LogResultNotSaved(logger, payout.OrderNumber, result.Accepted);
            return result.Accepted;
        }

        return result.Accepted;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Retrying payouts that were waiting for a bank account failed; the sweep will pick them up.")]
    private static partial void LogRetryFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The payout provider failed for order {OrderNumber}; it is retried later.")]
    private static partial void LogProviderFailed(ILogger logger, string orderNumber, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The payout attempt for order {OrderNumber} finished (accepted: {Accepted}) but its result could not be saved.")]
    private static partial void LogResultNotSaved(ILogger logger, string orderNumber, bool accepted);
}
