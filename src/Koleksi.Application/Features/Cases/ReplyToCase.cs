using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Application.Features.Cases;

/// <summary>Who has been asked something by the team and has not answered yet.</summary>
internal static class CaseThread
{
    /// <summary>
    /// True while the team's latest request to <paramref name="side"/> has no reply from that side after it. A request to the other
    /// side, or a reply by the other side, changes nothing here.
    /// </summary>
    public static bool IsAwaiting(IReadOnlyList<DisputeNote> notes, DisputeParty side)
    {
        var lastRequest = notes
            .Where(note => note.Kind == DisputeNoteKind.InfoRequest && note.To == side)
            .Select(note => (DateTimeOffset?)note.At)
            .Max();

        return lastRequest is { } asked
            && !notes.Any(note => note.Kind == DisputeNoteKind.Reply && note.To == side && note.At > asked);
    }

    /// <summary>The time of the team's latest request to either side, which is the time the staff-side marker carries.</summary>
    public static DateTimeOffset? LastRequestAt(IReadOnlyList<DisputeNote> notes) =>
        notes.Where(note => note.Kind == DisputeNoteKind.InfoRequest).Select(note => (DateTimeOffset?)note.At).Max();
}

/// <summary>The answer to a reply. Recorded for an idempotent replay.</summary>
public sealed record ReplyAccepted(bool Done = true, bool NotFound = false);

/// <summary>
/// A buyer's or seller's answer to the team's request for information, written to the case file for staff to read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may reply.</b> Only the buyer or seller of the order, taken from the signed-in account and never from the request. A
/// person who is neither is told the case does not exist, exactly as for an order that does not exist. A reply needs an
/// unanswered request addressed to that side (<see cref="CaseThread.IsAwaiting"/>); the site is not a free chat with staff.
/// </para>
/// <para>
/// <b>What it changes.</b> The reply is added to the case file and, once every side that was asked has answered, the staff-side
/// "waiting on information" marker is cleared so the case stops showing as waiting. The other side never sees the reply. The
/// order itself is not rewritten: the transaction only checks it is still disputed, so a reply racing a ruling is refused
/// (<c>case.closed</c>) rather than written to a closed case.
/// </para>
/// </remarks>
public sealed class ReplyToCaseHandler(
    ICurrentUser user,
    IOrderRepository orders,
    ICaseStore store,
    IClock clock)
{
    public async Task<ReplyAccepted> HandleAsync(string orderId, string? text, CancellationToken ct)
    {
        var callerId = user.RequireUserId();

        if (!EntityId.IsValid(orderId))
        {
            return new ReplyAccepted(Done: false, NotFound: true);
        }

        var order = await orders.FindAsync(EntityId.Normalise(orderId), ct).ConfigureAwait(false);
        var side = order is { Dispute: not null } ? SideOf(order, callerId) : null;
        if (order is null || side is null)
        {
            return new ReplyAccepted(Done: false, NotFound: true);
        }

        if (order.EscrowState != EscrowState.Disputed)
        {
            throw DomainException.For("case.closed", "This case has already been ruled on.");
        }

        var notes = await store.ListSharedNotesAsync(order.Id, ct).ConfigureAwait(false);
        if (!CaseThread.IsAwaiting(notes, side.Value))
        {
            throw DomainException.For("case.no_request_pending", "The team is not waiting for an answer from you on this case.");
        }

        var reply = DisputeNote.CreateReply(order.Id, callerId, side.Value, text, clock);

        // Is anyone still being waited on once this reply is in? The other side, if it was asked and has not answered.
        var other = side == DisputeParty.Buyer ? DisputeParty.Seller : DisputeParty.Buyer;
        var clear = CaseThread.IsAwaiting(notes, other) ? null : CaseThread.LastRequestAt(notes);

        try
        {
            await store.AddReplyAsync(new CaseReplyCommit(order, reply, clear), ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            // Either the case was ruled on, or staff asked again while this was being written. Say which.
            var current = await orders.FindAsync(order.Id, ct).ConfigureAwait(false);
            if (current is null || current.EscrowState != EscrowState.Disputed)
            {
                throw DomainException.For("case.closed", "This case has already been ruled on.");
            }

            throw DomainException.For("case.reply_conflict", "The team wrote to you while you were replying. Read the new message and reply again.");
        }

        return new ReplyAccepted();
    }

    private static DisputeParty? SideOf(Order order, string callerId) =>
        string.Equals(order.BuyerId, callerId, StringComparison.Ordinal) ? DisputeParty.Buyer
        : string.Equals(order.SellerId, callerId, StringComparison.Ordinal) ? DisputeParty.Seller
        : null;
}
