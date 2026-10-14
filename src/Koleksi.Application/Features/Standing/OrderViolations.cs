using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;

namespace Koleksi.Application.Features.Standing;

/// <summary>Turns an order that ended unpaid into the violation it represents, if it represents one.</summary>
internal static class OrderViolations
{
    /// <summary>
    /// What an unpaid, ended order of this kind counts as, and which deal it belongs to; <c>null</c> for a
    /// plain sale, which is not a violation (its units just go back on the shelf).
    /// </summary>
    public static (ViolationType Type, string Reference)? Of(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return order.Source switch
        {
            OrderSource.AuctionWin => (ViolationType.AuctionWinNotPaid, $"auction:{order.SourceReference ?? order.Id}"),
            OrderSource.AcceptedOffer => (ViolationType.AcceptedOfferNotPaid, $"offer:{order.SourceReference ?? order.Id}"),
            _ => null,
        };
    }

    /// <summary>
    /// Counts the order against its buyer when it was a commitment. Best effort: the order is already
    /// cancelled, and a failure here must not turn a cancel into an error. A missed warning is found again
    /// by the auction sweep for an auction win, and is otherwise lost, so the failure is logged by the
    /// standing service.
    /// </summary>
    public static async Task RecordIfCommitmentAsync(AccountStandingService standing, Order order, CancellationToken ct)
    {
        if (Of(order) is not { } violation)
        {
            return;
        }

        try
        {
            await standing.RecordViolationAsync(order.BuyerId, violation.Type, violation.Reference, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The cancel has happened; do not undo it over bookkeeping.
            _ = failure;
        }
    }
}
