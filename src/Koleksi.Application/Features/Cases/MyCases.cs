using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Cases;

// The JSON the public site reads. Camel case on the wire, money in whole rupiah, times ISO 8601 UTC. Nothing here carries an
// account id, a staff id or a staff name: the staff side is "the Koleksi team" and the other party is not named at all.

/// <summary>One message from the team in a case: a request for information, or the ruling's reason.</summary>
/// <param name="Kind"><c>infoRequest</c>, <c>reply</c> (this person's own answer) or <c>decision</c>. Never a staff-internal note.</param>
public sealed record CaseMessageView(string Id, string Kind, DateTimeOffset At, string Text);

/// <summary>The refund a buyer was granted: recorded, not paid. See <see cref="RefundStatus"/>.</summary>
/// <param name="AmountRupiah">What was granted. It can be less than <see cref="CaseView.TotalRupiah"/>.</param>
public sealed record CaseRefundView(long AmountRupiah, string Status);

/// <param name="Role">The caller's side: <c>buyer</c> or <c>seller</c>.</param>
/// <param name="Status"><c>open</c>, <c>awaitingInfo</c> (the team asked <b>this person</b> something) or <c>resolved</c>.</param>
/// <param name="Outcome"><c>refundToBuyer</c> or <c>releaseToSeller</c> once ruled on, otherwise <c>null</c>.</param>
/// <param name="YourReason">What the buyer wrote when opening the case. Only the buyer who wrote it gets it back.</param>
/// <param name="TotalRupiah">What the buyer paid for the order (items plus shipping), so a partial refund reads as a part of it.</param>
/// <param name="IsNew">The team wrote to this person about the case and they had not been shown it before this read.</param>
public sealed record CaseView(
    string OrderId,
    string OrderNumber,
    string Title,
    string Role,
    string Status,
    string? Outcome,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    string? YourReason,
    long TotalRupiah,
    bool IsNew,
    IReadOnlyList<CaseMessageView> Messages,
    CaseRefundView? Refund);

/// <param name="More">There are older cases than the ones listed.</param>
public sealed record CasesView(IReadOnlyList<CaseView> Items, bool More);

/// <summary>A refund as the buyer's Payouts tab lists it.</summary>
/// <param name="Status"><c>recordedPendingGateway</c> or <c>simulated</c>. Neither means the money moved.</param>
/// <param name="OrderTotalRupiah">What the buyer paid for the order, so a partial refund reads as a part of it.</param>
public sealed record RefundEntryView(
    string OrderNumber, long AmountRupiah, long OrderTotalRupiah, string Reason, string Status, DateTimeOffset CreatedAt);

/// <param name="Unavailable">The refunds could not be read just now. The list is empty because of that, not because there are none.</param>
public sealed record RefundsView(IReadOnlyList<RefundEntryView> Items, bool Unavailable);

/// <summary>
/// The caller's own disputes: their status, what the team asked them, and the ruling with its reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may see what.</b> The orders come from the caller's own buyer and seller lists, and each is checked again here to
/// have the caller as its buyer or seller, so another person's case is never read, let alone returned. The store cannot return
/// a staff-internal note. An information request is returned only to the side it is addressed to, and the status
/// <c>awaitingInfo</c> is shown only to that side too: the other party sees the case as <c>open</c>, so they do not learn that
/// the team is asking the other side something. The ruling's reason is shown to both sides. The buyer's refund amount is shown
/// to the buyer only.
/// </para>
/// <para>
/// <b>Notices.</b> Reading the list is what delivers a notice: the ones that were pending are marked delivered once the view is
/// built, and the cases they were about come back with <c>isNew</c>. Opening the page twice shows them as new once.
/// </para>
/// </remarks>
public sealed class ListMyCasesHandler(
    ICurrentUser user,
    IOrderRepository orders,
    ICaseStore store,
    IClock clock)
{
    /// <summary>The newest cases listed. Older ones are reported as <see cref="CasesView.More"/>, not hidden silently.</summary>
    public const int MaxCases = 50;

    private const int NoteReadConcurrency = 8;

    public async Task<CasesView> HandleAsync(CancellationToken ct)
    {
        var callerId = user.RequireUserId();

        // One read finds every dispute this person is in, on either side: a pointer row per dispute, not their whole order history.
        var mine = (await orders.ListDisputedForUserAsync(callerId, ct).ConfigureAwait(false))
            .Where(order => RoleOf(order, callerId) is not null)
            .DistinctBy(order => order.Id, StringComparer.Ordinal)
            .OrderByDescending(Activity)
            .ToList();

        var shown = mine.Take(MaxCases).ToList();
        var noticesRead = store.ListPendingNoticesAsync(callerId, ct);
        var refundsRead = store.FindRefundsAsync(
            [.. shown.Where(o => o.BuyerId == callerId && o.Dispute is { Resolution: DisputeResolution.RefundToBuyer }).Select(o => o.Id)],
            ct);
        var notesRead = ReadNotesAsync(shown, ct);
        await Task.WhenAll(noticesRead, refundsRead, notesRead).ConfigureAwait(false);

        var notices = noticesRead.Result;
        var refunds = refundsRead.Result;
        var notes = notesRead.Result;

        // Everything above was read for this person; delivering the notices comes last, so a read that failed marks nothing.
        // Only the notices of cases that were listed are marked: one about an older case that was not shown stays pending.
        var shownIds = shown.Select(order => order.Id).ToHashSet(StringComparer.Ordinal);
        var delivered = await store
            .MarkNoticesDeliveredAsync(callerId, [.. notices.Where(notice => shownIds.Contains(notice.OrderId))], clock.UtcNow, ct)
            .ConfigureAwait(false);

        // "New" is what this very read delivered: of several simultaneous reads of one fresh notice, one gets it.
        var newOrderIds = delivered.Select(notice => notice.OrderId).ToHashSet(StringComparer.Ordinal);

        // What is waiting on this person comes first; everything else keeps the newest-activity order (OrderBy is stable).
        var items = shown
            .Select(order => ToView(order, callerId, notes[order.Id], newOrderIds.Contains(order.Id), refunds.GetValueOrDefault(order.Id)))
            .OrderBy(item => item.Status == "awaitingInfo" ? 0 : 1)
            .ToList();

        return new CasesView(items, mine.Count > shown.Count);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<DisputeNote>>> ReadNotesAsync(IReadOnlyList<Order> cases, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<DisputeNote>>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(NoteReadConcurrency);

        var reads = cases.Select(async order =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return (order.Id, Notes: await store.ListSharedNotesAsync(order.Id, ct).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        foreach (var (id, notes) in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            result[id] = notes;
        }

        return result;
    }

    private static CaseView ToView(Order order, string callerId, IReadOnlyList<DisputeNote> notes, bool isNew, Refund? refund)
    {
        var role = RoleOf(order, callerId)!;
        var side = role == "buyer" ? DisputeParty.Buyer : DisputeParty.Seller;
        var dispute = order.Dispute!;

        // A side is "awaiting info" while the case is open and the team's latest request to it has not been answered by it. Only
        // requests addressed to this side count, so the other side's status says nothing about what the team asked someone else
        // (and a later request to the other side does not clear this one).
        var status = dispute.Resolution is not null ? "resolved"
            : CaseThread.IsAwaiting(notes, side) ? "awaitingInfo"
            : "open";

        // Belt and braces: the store never returns a Note, and a request or reply that belongs to the other side is dropped here.
        var messages = notes
            .Where(note => note.Kind == DisputeNoteKind.Decision
                || (note.Kind is DisputeNoteKind.InfoRequest or DisputeNoteKind.Reply && note.To == side))
            .OrderBy(note => note.At)
            .Select(note => new CaseMessageView(
                note.Id,
                note.Kind switch
                {
                    DisputeNoteKind.Decision => "decision",
                    DisputeNoteKind.Reply => "reply",
                    _ => "infoRequest",
                },
                note.At,
                note.Text))
            .ToList();

        return new CaseView(
            order.Id,
            order.OrderNumber,
            order.Lines.Count > 0 ? order.Lines[0].Title : order.OrderNumber,
            role,
            status,
            dispute.Resolution switch
            {
                DisputeResolution.RefundToBuyer => "refundToBuyer",
                DisputeResolution.ReleaseToSeller => "releaseToSeller",
                _ => null,
            },
            dispute.OpenedAt,
            dispute.ResolvedAt,
            role == "buyer" ? dispute.Reason : null,
            order.GrandTotal.Rupiah,
            isNew,
            messages,
            role == "buyer" && refund is not null ? new CaseRefundView(refund.Amount.Rupiah, RefundStatusOf(refund)) : null);
    }

    internal static string RefundStatusOf(Refund refund) =>
        refund.Status == RefundStatus.Simulated ? "simulated" : "recordedPendingGateway";

    private static DateTimeOffset Activity(Order order) => order.Dispute?.ResolvedAt ?? order.Dispute?.OpenedAt ?? order.CreatedAt;

    private static string? RoleOf(Order order, string callerId) =>
        string.Equals(order.BuyerId, callerId, StringComparison.Ordinal) ? "buyer"
        : string.Equals(order.SellerId, callerId, StringComparison.Ordinal) ? "seller"
        : null;
}

/// <summary>
/// The refunds granted to the caller as a buyer, for the Payouts tab: one entry per refunded order.
/// </summary>
/// <remarks>
/// A refund is a record, not a payment. No payment gateway exists, so nothing here means money reached anyone; the status says
/// "recorded, payment pending gateway" (or "simulated" on a development stage). The refunds are found through the caller's own
/// disputed orders (<see cref="IOrderRepository.ListDisputedForUserAsync"/>) and read with one batched get.
/// </remarks>
public sealed partial class ListMyRefundsHandler(
    ICurrentUser user, IOrderRepository orders, ICaseStore store, ILogger<ListMyRefundsHandler> logger)
{
    /// <summary>
    /// The refunds, or an empty list marked <see cref="RefundsView.Unavailable"/> when they cannot be read. A seller's payouts are
    /// listed beside them and must not be lost because the buyer-side lookup failed.
    /// </summary>
    public async Task<RefundsView> HandleAsync(CancellationToken ct)
    {
        var callerId = user.RequireUserId();

        try
        {
            return new RefundsView(await ReadAsync(callerId, ct).ConfigureAwait(false), Unavailable: false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogRefundsUnavailable(logger, failure);
            return new RefundsView([], Unavailable: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A buyer's refunds could not be read; the payouts list is answered without them.")]
    private static partial void LogRefundsUnavailable(ILogger logger, Exception failure);

    private async Task<IReadOnlyList<RefundEntryView>> ReadAsync(string callerId, CancellationToken ct)
    {
        var refunded = (await orders.ListDisputedForUserAsync(callerId, ct).ConfigureAwait(false))
            .Where(order => string.Equals(order.BuyerId, callerId, StringComparison.Ordinal)
                && order.Dispute is { Resolution: DisputeResolution.RefundToBuyer })
            .ToList();
        if (refunded.Count == 0)
        {
            return [];
        }

        var refunds = await store.FindRefundsAsync([.. refunded.Select(order => order.Id)], ct).ConfigureAwait(false);
        var totals = refunded.ToDictionary(order => order.Id, order => order.GrandTotal.Rupiah, StringComparer.Ordinal);

        // The record's own buyer must be the caller too: the key is the order's, and the order is the caller's, but a row that
        // somehow named someone else is not shown.
        return [.. refunds.Values
            .Where(refund => string.Equals(refund.BuyerId, callerId, StringComparison.Ordinal) && totals.ContainsKey(refund.OrderId))
            .OrderByDescending(refund => refund.CreatedAt)
            .Select(refund => new RefundEntryView(
                refund.OrderNumber, refund.Amount.Rupiah, totals[refund.OrderId], refund.Reason, ListMyCasesHandler.RefundStatusOf(refund), refund.CreatedAt))];
    }
}
