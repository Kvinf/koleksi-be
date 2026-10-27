using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Application.Features.Orders;

/// <summary>
/// Caps how many unpaid orders one buyer can have open at once.
/// </summary>
/// <remarks>
/// <para>
/// Every checkout reserves its items for a payment window, so without a cap one account could
/// check out the whole catalogue in a loop and, by never paying, keep it all off the shelf.
/// Ten is far more than a real buyer has awaiting payment at the same moment.
/// </para>
/// <para>
/// An earlier unpaid order that covers an item being checked out again is not counted: a repeat
/// checkout by the same buyer supersedes it (see <see cref="OrderHolds"/>), so retrying one item
/// never runs into the cap.
/// </para>
/// <para>
/// <b>Holding without paying.</b> A hold lasts fifteen minutes and a buyer may check out again the moment
/// it lapses, so one account could keep a single unit off the shelf for ever by re-checking out every
/// fifteen minutes and never paying. A buyer may therefore start at most <see cref="MaxAttemptsPerItem"/>
/// unpaid checkouts of the same item (cancelled or still waiting) within <see cref="AttemptWindow"/>; the
/// next one is refused with <c>order.too_many_attempts</c>. That is generous for a buyer who changes an
/// address or a quantity, and caps a hoarder at three holds in two hours. Offers and auction wins are not
/// counted: not paying for those already costs a warning.
/// </para>
/// <para>
/// Reads the buyer's newest orders and stops once it reaches ones older than the attempt window. Ids are
/// time-ordered, so that is a few small reads, not the buyer's whole history.
/// </para>
/// </remarks>
internal static class PendingOrderLimit
{
    public const int MaxPending = 10;

    /// <summary>The most unpaid checkouts of one item a buyer may start within <see cref="AttemptWindow"/>.</summary>
    public const int MaxAttemptsPerItem = 3;

    public static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(2);

    private const int PageSize = 20;
    private const int MaxPages = 3;

    /// <exception cref="DomainException"><c>order.too_many_pending</c>: this checkout would exceed the cap.</exception>
    public static async Task EnsureRoomAsync(
        IOrderRepository orders, IClock clock, string buyerId, IReadOnlyList<Order> incoming, CancellationToken ct)
    {
        var incomingListings = incoming
            .SelectMany(order => order.Lines.Select(line => line.ListingId))
            .ToHashSet(StringComparer.Ordinal);

        var pendingCutoff = clock.UtcNow - Order.PaymentWindow;
        var attemptCutoff = clock.UtcNow - AttemptWindow;
        var pending = 0;
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? cursor = null;

        for (var pageNumber = 0; pageNumber < MaxPages; pageNumber++)
        {
            var page = await orders.ListForBuyerAsync(buyerId, cursor, PageSize, ct).ConfigureAwait(false);

            var reachedLapsed = false;
            foreach (var order in page.Items)
            {
                if (order.CreatedAt <= attemptCutoff)
                {
                    reachedLapsed = true;
                    break;
                }

                // An unpaid sale of an item being checked out now: one more attempt at it. Paid, shipped and
                // other finished orders are not attempts, and neither are offers and auction wins.
                if (order.Source == OrderSource.FixedPrice
                    && order.EscrowState is EscrowState.AwaitingPayment or EscrowState.Cancelled)
                {
                    foreach (var line in order.Lines.Where(line => incomingListings.Contains(line.ListingId)))
                    {
                        attempts[line.ListingId] = attempts.GetValueOrDefault(line.ListingId) + 1;
                    }
                }

                if (order.CreatedAt > pendingCutoff
                    && order.EscrowState == EscrowState.AwaitingPayment
                    && !order.Lines.Any(line => incomingListings.Contains(line.ListingId)))
                {
                    pending++;
                }
            }

            if (reachedLapsed || page.NextCursor is null)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        if (incoming.Any(order => order.Source == OrderSource.FixedPrice)
            && attempts.Values.Any(count => count >= MaxAttemptsPerItem))
        {
            throw DomainException.For(
                "order.too_many_attempts",
                "You have started checkout for this item several times without paying. Try again later.");
        }

        if (pending + incoming.Count > MaxPending)
        {
            throw DomainException.For(
                "order.too_many_pending",
                $"You already have {MaxPending} orders waiting for payment. Pay for or cancel one first.");
        }
    }
}
