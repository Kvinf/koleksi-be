using Koleksi.Domain.Orders;

namespace Koleksi.Application.Abstractions;

/// <summary>A buyer's or seller's answer to a request for information, ready to be committed in one transaction.</summary>
/// <param name="Order">The order, as read. It must still be <c>Disputed</c> when the commit lands.</param>
/// <param name="Reply">The reply (<see cref="DisputeNoteKind.Reply"/>).</param>
/// <param name="ClearAwaitingFor">
/// When every side that was asked something has now answered, the time of the latest request (the time the staff-side "waiting on
/// information" marker carries): the marker is removed only if it still has that time, so a request staff made in the meantime
/// is not wiped. <c>null</c> leaves the marker alone (the other side is still being waited on).
/// </param>
public sealed record CaseReplyCommit(Order Order, DisputeNote Reply, DateTimeOffset? ClearAwaitingFor);

/// <summary>
/// What the public site reads about a dispute that staff wrote: the messages meant for the parties, the notices owed to them
/// and the refund. Read-only apart from marking a notice delivered and recording a party's reply.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not <see cref="IDisputeAdminStore"/>: that one belongs to the admin API and carries the staff-internal
/// notes and every write. This interface cannot return a <see cref="DisputeNoteKind.Note"/> at all, so no caller can leak one.
/// </para>
/// <para>
/// None of these methods checks who is asking. Each takes an id that the caller has already tied to the signed-in person
/// (an order from that person's own order list, that person's own id), and every read is a query or batch get on that key:
/// never a scan.
/// </para>
/// </remarks>
public interface ICaseStore
{
    /// <summary>
    /// The case messages that are shown to the parties, oldest first: information requests, replies to them and the decision.
    /// Staff-internal notes are not returned. Each request names who it is addressed to, and each reply which side wrote it
    /// (<see cref="DisputeNote.To"/>); the caller must show
    /// a request only to that side.
    /// </summary>
    Task<IReadOnlyList<DisputeNote>> ListSharedNotesAsync(string orderId, CancellationToken ct);

    /// <summary>The person's notices that have not been shown to them yet, newest first. Only their own partition is read.</summary>
    Task<IReadOnlyList<OrderNotice>> ListPendingNoticesAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Marks the person's notices delivered, each one conditionally on it still being pending.
    /// </summary>
    /// <returns>
    /// The notices <b>this call</b> delivered. One a parallel read delivered first is not in the list, so only one of several
    /// simultaneous reads tells the person it is new.
    /// </returns>
    Task<IReadOnlyList<OrderNotice>> MarkNoticesDeliveredAsync(string userId, IReadOnlyCollection<OrderNotice> notices, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Writes a reply to the case file. One transaction: the order is only checked (still <c>Disputed</c>), the reply is added, and the
    /// "waiting on information" marker is removed when <see cref="CaseReplyCommit.ClearAwaitingFor"/> says nobody is waited on.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">
    /// The order is no longer disputed, or staff asked something again while the reply was being written.
    /// </exception>
    Task AddReplyAsync(CaseReplyCommit commit, CancellationToken ct);

    /// <summary>The refunds recorded for these orders, by order id. An order with no refund is absent. One batched read.</summary>
    Task<IReadOnlyDictionary<string, Refund>> FindRefundsAsync(IReadOnlyCollection<string> orderIds, CancellationToken ct);
}
