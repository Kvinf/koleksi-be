using Koleksi.Domain.Admin;
using Koleksi.Domain.Orders;

namespace Koleksi.Application.Abstractions;

/// <summary>
/// Everything one staff change to a dispute writes, to be committed together: the commit either happens whole, with its
/// audit entry, or not at all.
/// </summary>
/// <param name="Order">The order, as read. Its state must still be <c>Disputed</c> when the commit lands.</param>
/// <param name="Note">The note or information request.</param>
/// <param name="MarkAwaitingInfo">True for an information request: the case now waits on that side.</param>
/// <param name="Notices">Who is owed a message about it (pending; see <see cref="OrderNotice"/>).</param>
/// <param name="Audit">The audit entry, written in the same transaction.</param>
public sealed record DisputeNoteCommit(
    Order Order,
    DisputeNote Note,
    bool MarkAwaitingInfo,
    IReadOnlyList<OrderNotice> Notices,
    AuditEntry Audit);

/// <summary>A ruling, ready to be committed in one transaction.</summary>
/// <param name="Order">The order after <see cref="Order.ResolveDispute"/>.</param>
/// <param name="ExpectedVersion">The version it was read at; the commit fails if the order has moved on (another staff member ruled first).</param>
/// <param name="Payout">For a ruling for the seller: the debt to the seller, written with the release exactly as <c>EscrowReleaseService</c> does.</param>
/// <param name="Refund">For a ruling for the buyer: the refund record.</param>
/// <param name="Decision">The ruling's reason, as a note on the case.</param>
/// <param name="Notices">One for each side.</param>
/// <param name="Audit">The audit entry, in the same transaction.</param>
public sealed record DisputeResolutionCommit(
    Order Order,
    long ExpectedVersion,
    Payout? Payout,
    Refund? Refund,
    DisputeNote Decision,
    IReadOnlyList<OrderNotice> Notices,
    AuditEntry Audit);

/// <summary>A decision about a held remainder, ready to be committed in one transaction.</summary>
/// <param name="Order">The refunded order. Its state must still be <c>Refunded</c> when the commit lands.</param>
/// <param name="Refund">The refund after <see cref="Refund.SettleRemainder"/>. The commit moves it only if it is still held.</param>
/// <param name="Payout">What the seller is owed out of the remainder, written beside it.</param>
/// <param name="Audit">The audit entry, in the same transaction.</param>
public sealed record RemainderSettlementCommit(Order Order, Refund Refund, Payout Payout, AuditEntry Audit);

/// <summary>
/// The back office's view of disputes, and the only way staff changes to one are written.
/// </summary>
/// <remarks>
/// <para>
/// Queues are queries on keys the order itself carries while it is in the state (an open dispute sits in a sparse index
/// ordered by when it was opened, a resolved one in another ordered by when it was resolved). Nothing here scans.
/// </para>
/// <para>
/// Every write is one DynamoDB transaction that includes the audit rows, and fails with
/// <see cref="ConcurrencyConflictException"/> when the order is no longer in the state the change needed (two staff rulings at
/// once: one wins, the other gets that exception).
/// </para>
/// </remarks>
public interface IDisputeAdminStore
{
    /// <summary>The open disputes, oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Order>> ListOpenAsync(int limit, CancellationToken ct);

    /// <summary>The resolved disputes, most recently resolved first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Order>> ListResolvedAsync(int limit, CancellationToken ct);

    /// <summary>Case notes, oldest first.</summary>
    Task<IReadOnlyList<DisputeNote>> ListNotesAsync(string orderId, CancellationToken ct);

    /// <summary>Which of these orders have an information request staff are waiting on. One batched read.</summary>
    Task<IReadOnlySet<string>> AwaitingInfoAsync(IReadOnlyCollection<string> orderIds, CancellationToken ct);

    Task<Refund?> FindRefundAsync(string orderId, CancellationToken ct);

    /// <exception cref="ConcurrencyConflictException">The order is no longer disputed.</exception>
    Task AddNoteAsync(DisputeNoteCommit commit, CancellationToken ct);

    /// <exception cref="ConcurrencyConflictException">The order changed since it was read, or was already ruled on.</exception>
    Task ResolveAsync(DisputeResolutionCommit commit, CancellationToken ct);

    /// <exception cref="ConcurrencyConflictException">The remainder is no longer held (settled by someone else), or the order changed state.</exception>
    Task SettleRemainderAsync(RemainderSettlementCommit commit, CancellationToken ct);

    /// <summary>Saves what the refund sender did with a recorded refund. Only moves a refund still in <see cref="RefundStatus.Recorded"/>.</summary>
    Task SaveRefundOutcomeAsync(Refund refund, CancellationToken ct);
}

/// <summary>
/// Sends a refund to the buyer. A seam, because there is no payment gateway to send through yet.
/// </summary>
/// <remarks>
/// The real implementation records "payment pending gateway" and moves no money. The simulated one (development stage only,
/// behind the same switch and stage guard as the other simulators) rehearses the flow, and also moves no money. The pages say
/// "refund recorded, payment pending gateway" for both and never imply money moved.
/// </remarks>
public interface IRefundSender
{
    /// <returns>Where the refund stands after the attempt: never <see cref="RefundStatus.Recorded"/>.</returns>
    Task<RefundStatus> SendAsync(Refund refund, CancellationToken ct);
}

public enum HolidayWrite
{
    Written = 0,
    AlreadyExists = 1,
}

public enum HolidayRemoval
{
    Removed = 0,
    NotFound = 1,
}

/// <summary>The holiday list the business-day count skips. Each change is one transaction with its audit entry.</summary>
public interface IHolidayStore
{
    /// <summary>One year's holidays, in date order. A query on one partition.</summary>
    Task<IReadOnlyList<Holiday>> ListAsync(int year, CancellationToken ct);

    Task<HolidayWrite> AddAsync(Holiday holiday, AuditEntry audit, CancellationToken ct);

    /// <param name="audit">Built by the caller from what <see cref="FindAsync"/> returned; written only when the holiday still exists.</param>
    Task<HolidayRemoval> RemoveAsync(string isoDate, AuditEntry audit, CancellationToken ct);

    Task<Holiday?> FindAsync(string isoDate, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="holidays"/> once per year: a marker row makes a second call (or a second Lambda starting at the
    /// same moment) do nothing, so a holiday staff deliberately removed is never put back.
    /// </summary>
    /// <returns><c>true</c> when this call seeded the year.</returns>
    Task<bool> SeedAsync(int year, IReadOnlyList<Holiday> holidays, AuditEntry audit, CancellationToken ct);
}
