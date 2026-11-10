using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Bounties;
using Koleksi.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Sweeps;

/// <summary>
/// Settles Dicari whose clock ran out: expires the ones nobody dealt on, and looks again at the ones with a deal in
/// flight, whose accepted offer may have lapsed or whose order may have been paid.
/// </summary>
/// <remarks>
/// Reads the sparse due index, so a run costs a handful of queries however many Dicari exist. Every item is re-checked
/// by <see cref="BountyDeals.ReconcileAsync"/> and written with a version condition, so a repeated tick is harmless.
/// </remarks>
public sealed partial class ExpireDueBountiesHandler(
    IBountyRepository bounties, BountyDeals deals, IClock clock, ILogger<ExpireDueBountiesHandler> logger)
{
    public async Task<ExpireDueBountiesResult> RunAsync(SweepBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var due = await bounties.ListDueAsync(clock.UtcNow, budget.MaxItemsPerDuty, ct).ConfigureAwait(false);

        int settled = 0, errors = 0;
        foreach (var bounty in due)
        {
            ct.ThrowIfCancellationRequested();
            if (budget.IsTimeUp)
            {
                break;
            }

            try
            {
                if (await deals.ReconcileAsync(bounty, ct).ConfigureAwait(false))
                {
                    settled++;
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                errors++;
                LogSettleFailed(logger, bounty.Id, failure);
            }
        }

        return new ExpireDueBountiesResult(settled, errors);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Dicari {BountyId} could not be settled this run; it is still due and is retried next run.")]
    private static partial void LogSettleFailed(ILogger logger, string bountyId, Exception exception);
}

/// <param name="Settled">Dicari that changed: expired, fulfilled, or given back after a failed deal.</param>
public sealed record ExpireDueBountiesResult(int Settled, int Errors);
