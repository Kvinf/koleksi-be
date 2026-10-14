namespace Koleksi.Application.Abstractions;

/// <summary>
/// One-off maintenance: gives orders written before the sweep's index keys existed the keys they
/// should have, so the unpaid-order and auto-release sweeps can find them.
/// </summary>
/// <remarks>
/// <para>
/// The sweeps read a sparse index (see <c>IOrderRepository.ListPaymentExpiredAsync</c>), so an order
/// stored without its sweep keys is invisible to them: an unpaid one is never cancelled and a
/// delivered one is never auto-released (QA D-12). Orders written since carry the keys already.
/// </para>
/// <para>
/// Bounded and resumable, and safe to run again: each call looks at a fixed number of stored rows
/// and returns the cursor to continue from, or <c>null</c> when the table has been covered. It reads
/// by scanning, which is why it is a manual internal route and never on a user request path.
/// </para>
/// </remarks>
public interface IOrderSweepBackfill
{
    Task<OrderSweepBackfillPage> BackfillAsync(string? cursor, int limit, CancellationToken ct);
}

/// <param name="Examined">Stored rows read in this step (of any kind).</param>
/// <param name="Updated">Orders that were missing their sweep keys and now have them.</param>
/// <param name="NextCursor">Pass back to continue; <c>null</c> when everything has been covered.</param>
public sealed record OrderSweepBackfillPage(int Examined, int Updated, string? NextCursor);
