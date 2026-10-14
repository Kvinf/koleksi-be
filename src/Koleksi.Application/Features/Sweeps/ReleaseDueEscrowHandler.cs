using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Sweeps;

/// <summary>
/// Releases escrow on delivered orders whose confirmation window has closed without the buyer
/// confirming or disputing.
/// </summary>
/// <remarks>
/// <para>
/// This is the "two days after delivery" half of the escrow promise. It releases through
/// <see cref="EscrowReleaseService"/> with <c>confirmedByBuyer: false</c>, which refuses while the window
/// is still open and while a dispute is open, so the rule stays in the domain and this only
/// supplies the schedule. The release also records the payout the seller is owed.
/// </para>
/// <para>
/// The due index only holds delivered orders. An order becomes <c>Delivered</c> when the courier
/// reports it (<see cref="TrackShipmentsHandler"/>) or the buyer confirms; with no courier-tracking
/// provider configured this finds nothing and only the buyer's confirmation releases funds.
/// </para>
/// </remarks>
public sealed partial class ReleaseDueEscrowHandler(
    IOrderRepository orders,
    EscrowReleaseService release,
    IClock clock,
    ILogger<ReleaseDueEscrowHandler> logger)
{
    public async Task<ReleaseDueEscrowResult> RunAsync(SweepBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var now = clock.UtcNow;
        var due = await orders.ListDueForAutoReleaseAsync(now, budget.MaxItemsPerDuty, ct).ConfigureAwait(false);

        int released = 0, skipped = 0, errors = 0;

        foreach (var order in due)
        {
            ct.ThrowIfCancellationRequested();

            if (budget.IsTimeUp)
            {
                LogTimeUp(logger, due.Count);
                break;
            }

            // The index can lag a dispute or a buyer's own confirmation; only a delivered order
            // past its deadline is released.
            if (order.EscrowState != EscrowState.Delivered || order.AutoReleaseAt is not { } releaseAt || releaseAt > now)
            {
                skipped++;
                continue;
            }

            try
            {
                await release.ReleaseAsync(order, confirmedByBuyer: false, ct).ConfigureAwait(false);
                released++;
            }
            catch (Exception failure) when (failure is ConcurrencyConflictException or DomainException)
            {
                // Confirmed, disputed, or otherwise changed between the read and the write.
                skipped++;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                errors++;
                LogItemFailed(logger, order.Id, failure);
            }
        }

        LogSummary(logger, due.Count, released, skipped, errors);
        return new ReleaseDueEscrowResult(released, skipped, errors);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep release-escrow: {Due} due, {Released} released, {Skipped} skipped, {Errors} failed.")]
    private static partial void LogSummary(ILogger logger, int due, int released, int skipped, int errors);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Sweep release-escrow: could not release order {OrderId}; it stays due for the next run.")]
    private static partial void LogItemFailed(ILogger logger, string orderId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep release-escrow: time allowance used up with {Due} due; the rest wait for the next run.")]
    private static partial void LogTimeUp(ILogger logger, int due);
}

/// <param name="Released">Orders moved to <c>Released</c>.</param>
/// <param name="Skipped">Found due but confirmed, disputed, or changed by someone else.</param>
/// <param name="Errors">Failed unexpectedly; still due next run.</param>
public sealed record ReleaseDueEscrowResult(int Released, int Skipped, int Errors);
