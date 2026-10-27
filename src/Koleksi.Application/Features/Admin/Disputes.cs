using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Domain.Messaging;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Admin;

/// <summary>The answer to a change that has nothing to return. Recorded for an idempotent replay.</summary>
public sealed record ChangeDone(bool Done = true);

/// <param name="Remainder">
/// For a ruling for the buyer that refunds less than the order total: <c>hold</c> (the default), <c>release</c> or <c>split</c>.
/// Nothing for a full refund or a ruling for the seller.
/// </param>
/// <param name="SellerPayout">For <c>split</c>: what the seller is owed out of the remainder. For nothing else.</param>
public sealed record ResolveDisputeRequest(
    DisputeResolution Resolution, string? Reason, long? RefundAmount, string? Remainder = null, long? SellerPayout = null);

/// <param name="Mode"><c>release</c> or <c>split</c> (a held remainder is decided, not held again).</param>
public sealed record SettleRemainderRequest(string? Mode, long? SellerPayout, string? Reason);

public sealed record AddDisputeNoteRequest(string? Text, string? To);

/// <summary>Shared lookups for the dispute handlers.</summary>
internal static class DisputeLookup
{
    public const int MaxOpen = 200;

    public const int MaxResolved = 100;


    public const int ChatMessagesPerThread = 50;

    public const int ExcerptLength = 140;

    /// <summary>The order with a dispute, or <c>dispute.not_found</c> (404). An id that is not an order id is simply not found.</summary>
    public static async Task<Order> RequireWithDisputeAsync(IOrderRepository orders, string orderId, CancellationToken ct)
    {
        if (!EntityId.IsValid(orderId))
        {
            throw NotFound();
        }

        var order = await orders.FindAsync(EntityId.Normalise(orderId), ct).ConfigureAwait(false);
        return order is { Dispute: not null } ? order : throw NotFound();
    }

    public static DomainException NotFound() => DomainException.For("dispute.not_found", "There is no such dispute.");

    public static string StatusOf(Order order, bool awaitingInfo) =>
        order.Dispute!.Resolution is not null ? "resolved" : awaitingInfo ? "awaitingInfo" : "open";

    public static string Excerpt(string reason) =>
        reason.Length > ExcerptLength ? string.Concat(reason.AsSpan(0, ExcerptLength - 3), "...") : reason;

    public static string TitleOf(Order order) =>
        order.Lines.Count == 0 ? string.Empty
            : order.Lines.Count == 1 ? order.Lines[0].Title
            : $"{order.Lines[0].Title} (+{order.Lines.Count - 1})";

    public static string Name(DisputeResolution resolution) => resolution.ToString();

    /// <summary>
    /// The staff's choice for a remainder, or <c>null</c> when none was given. A seller amount without a way to handle it, or a way
    /// the page does not know, is <c>dispute.remainder_invalid</c>; the amounts themselves are checked by <see cref="Refund"/>.
    /// </summary>
    public static RemainderChoice? ParseRemainder(string? mode, long? sellerPayout)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return sellerPayout is null
                ? null
                : throw DomainException.For("dispute.remainder_invalid", "A seller amount needs a way to handle the remainder.");
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "hold" => new RemainderChoice(RemainderMode.Hold, sellerPayout),
            "release" => new RemainderChoice(RemainderMode.Release, sellerPayout),
            "split" => new RemainderChoice(RemainderMode.Split, sellerPayout),
            _ => throw DomainException.For("dispute.remainder_invalid", "The remainder is held, released to the seller, or split."),
        };
    }

    public static string DispositionName(RemainderDisposition disposition) => disposition switch
    {
        RemainderDisposition.Held => "held",
        RemainderDisposition.ReleasedToSeller => "releasedToSeller",
        RemainderDisposition.Split => "split",
        _ => "none",
    };
}

/// <summary>Turns user ids into the handle and name staff see. Read-only: it never mints a handle the way the public site's resolver can.</summary>
internal sealed class PartyDirectory(IReadOnlyDictionary<string, Koleksi.Domain.Users.PublicProfile> profiles)
{
    private const string Unnamed = "(tanpa nama)";

    public static async Task<PartyDirectory> LoadAsync(IPublicProfileRepository repository, IEnumerable<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct(StringComparer.Ordinal).ToList();
        var found = ids.Count == 0
            ? new Dictionary<string, Koleksi.Domain.Users.PublicProfile>()
            : (await repository.FindManyByUserAsync(ids, ct).ConfigureAwait(false)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new PartyDirectory(found);
    }

    public PartyRefView Ref(string userId) =>
        profiles.TryGetValue(userId, out var profile)
            ? new PartyRefView(userId, profile.Handle, profile.ShownName)
            : new PartyRefView(userId, Unnamed, Unnamed);
}

/// <summary>The dispute queue, open (oldest first) or resolved (newest first), optionally narrowed by order number or exact handle.</summary>
public sealed class ListDisputesHandler(IDisputeAdminStore store, IPublicProfileRepository profiles)
{
    public const int MaxQueryLength = 100;

    public async Task<IReadOnlyList<DisputeSummaryView>> HandleAsync(string? status, string? q, CancellationToken ct)
    {
        var open = status switch
        {
            "open" => true,
            "resolved" => false,
            _ => throw DomainException.For("admin.dispute_query_invalid", "The status must be open or resolved."),
        };

        var query = (q ?? string.Empty).Trim();
        if (query.Length > MaxQueryLength)
        {
            throw DomainException.For("admin.dispute_query_invalid", $"The search must be at most {MaxQueryLength} characters.");
        }

        var orders = open
            ? await store.ListOpenAsync(DisputeLookup.MaxOpen, ct).ConfigureAwait(false)
            : await store.ListResolvedAsync(DisputeLookup.MaxResolved, ct).ConfigureAwait(false);

        // An exact handle is looked up once and compared by account id. An order number is matched as written, ignoring case.
        string? handleOwner = null;
        if (query.Length > 0)
        {
            var handle = query.TrimStart('@').ToLowerInvariant();
            handleOwner = handle.Length > 0
                ? (await profiles.FindByHandleAsync(handle, ct).ConfigureAwait(false))?.UserId
                : null;

            orders = [.. orders.Where(o =>
                o.OrderNumber.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (handleOwner is not null && (o.BuyerId == handleOwner || o.SellerId == handleOwner)))];
        }

        var awaiting = open
            ? await store.AwaitingInfoAsync([.. orders.Select(o => o.Id)], ct).ConfigureAwait(false)
            : new HashSet<string>();
        var parties = await PartyDirectory.LoadAsync(profiles, orders.SelectMany(o => new[] { o.BuyerId, o.SellerId }), ct).ConfigureAwait(false);

        return [.. orders.Select(o => Summarise(o, awaiting.Contains(o.Id), parties))];
    }

    internal static DisputeSummaryView Summarise(Order order, bool awaitingInfo, PartyDirectory parties) =>
        new(
            order.Id,
            order.OrderNumber,
            parties.Ref(order.BuyerId),
            parties.Ref(order.SellerId),
            DisputeLookup.TitleOf(order),
            order.GrandTotal.Rupiah,
            order.Dispute!.OpenedAt,
            DisputeLookup.Excerpt(order.Dispute.Reason),
            DisputeLookup.StatusOf(order, awaitingInfo),
            order.Dispute.Resolution is { } resolution ? DisputeLookup.Name(resolution) : null,
            order.Dispute.ResolvedAt);
}

/// <summary>
/// One case: the order, the buyer's complaint, the shipment, only this order's chat, the case notes, the refund, and each side's earlier disputes.
/// </summary>
/// <remarks>
/// Reading the chat is an audited action (<c>dispute.chat_viewed</c>): a private conversation is shown to staff only when a
/// case needs it, and the log says who looked and when. History counts cover every dispute each side was ever in, found from a pointer row written with each dispute
/// (one query on one partition per person, then batched gets of exactly those orders), so a busy trader costs no more than their disputes.
/// </remarks>
public sealed class GetDisputeHandler(
    ICurrentStaff staff,
    IOrderRepository orders,
    IDisputeAdminStore store,
    IPublicProfileRepository profiles,
    IConversationRepository conversations,
    IAuditLog audit,
    IClock clock)
{
    public async Task<DisputeCaseView> HandleAsync(string orderId, CancellationToken ct)
    {
        var order = await DisputeLookup.RequireWithDisputeAsync(orders, orderId, ct).ConfigureAwait(false);
        var dispute = order.Dispute!;

        var notes = await store.ListNotesAsync(order.Id, ct).ConfigureAwait(false);
        var awaiting = await store.AwaitingInfoAsync([order.Id], ct).ConfigureAwait(false);
        var refund = await store.FindRefundAsync(order.Id, ct).ConfigureAwait(false);
        var parties = await PartyDirectory.LoadAsync(profiles, [order.BuyerId, order.SellerId], ct).ConfigureAwait(false);
        var chat = await ReadChatAsync(order, ct).ConfigureAwait(false);
        var history = await HistoryAsync(order, ct).ConfigureAwait(false);

        var summary = ListDisputesHandler.Summarise(order, awaiting.Contains(order.Id), parties);

        return new DisputeCaseView(
            summary.OrderId, summary.OrderNumber, summary.Buyer, summary.Seller, summary.ItemTitle, summary.Total,
            summary.OpenedAt, summary.ReasonExcerpt, summary.Status, summary.Resolution, summary.ResolvedAt,
            dispute.Reason,
            [.. order.Lines.Select(l => new OrderLineViewDto(l.ListingId, l.Title, l.UnitPrice.Rupiah, l.Quantity))],
            order.ItemsTotal.Rupiah,
            order.ShippingTotal.Rupiah,
            order.AppliedFeePercent,
            order.PlatformFee.Rupiah,
            order.SellerPayout.Rupiah,
            order.EscrowState.ToString(),
            order.CreatedAt,
            order.PaidAt,
            order.ShippedAt,
            order.DeliveredAt,
            new CourierView(order.Courier.CourierCode, order.Courier.ServiceName),
            order.Shipment?.TrackingNumber,

            // A shipment cannot exist without both proof photos (Shipment.Create requires them), so their presence is the shipment's.
            new ProofView(order.Shipment is not null, order.Shipment is not null),
            order.ShipTo.City,
            chat,
            [.. notes.Select(ToView)],
            refund is null
                ? null
                : new RefundViewDto(
                    refund.Amount.Rupiah, refund.Reason, refund.CreatedAt, "recordedPendingGateway", refund.StaffName,
                    refund.Remainder.Rupiah, DisputeLookup.DispositionName(refund.Disposition), refund.SellerPayout.Rupiah, refund.SettledAt),
            history);
    }

    private static CaseNoteView ToView(DisputeNote note) =>
        new(
            note.Id,
            note.At,
            note.StaffName,
            note.Kind switch
            {
                DisputeNoteKind.InfoRequest => "infoRequest",
                DisputeNoteKind.Reply => "reply",
                _ => "note",
            },

            // For a request, who it was asked of; for a reply, which side wrote it.
            note.To is { } to ? (to == DisputeParty.Buyer ? "buyer" : "seller") : null,
            note.Text);

    /// <summary>Only the thread(s) between this buyer and this seller about this order's listings. Nothing else of theirs.</summary>
    private async Task<IReadOnlyList<ChatLineView>> ReadChatAsync(Order order, CancellationToken ct)
    {
        var lines = new List<(DateTimeOffset At, ChatLineView Line)>();

        foreach (var listingId in order.Lines.Select(l => l.ListingId).Distinct(StringComparer.Ordinal))
        {
            var conversation = await conversations.FindAsync(Conversation.IdFor(listingId, order.BuyerId), ct).ConfigureAwait(false);
            if (conversation is null || conversation.SellerId != order.SellerId)
            {
                continue;
            }

            var messages = await conversations
                .ListMessagesAsync(conversation.Id, DisputeLookup.ChatMessagesPerThread, ct)
                .ConfigureAwait(false);

            foreach (var message in messages)
            {
                var from = message.SenderId == order.BuyerId ? "buyer" : message.SenderId == order.SellerId ? "seller" : null;
                if (from is not null)
                {
                    lines.Add((message.SentAt, new ChatLineView(message.SentAt, from, message.Body)));
                }
            }
        }

        if (lines.Count > 0)
        {
            var entry = AuditEntry.Create(
                clock, staff.StaffId, staff.StaffName, AuditActions.DisputeChatViewed, AuditTargets.Order, order.Id, staff.RequestId,
                after: $"{lines.Count} messages");
            await audit.AppendAsync(entry, ct).ConfigureAwait(false);
        }

        return [.. lines.OrderBy(l => l.At).Select(l => l.Line)];
    }

    private async Task<DisputeHistoryView> HistoryAsync(Order order, CancellationToken ct)
    {
        // Each person's disputes come from their pointer rows, on either side; the buyer's count keeps only the ones where they bought,
        // the seller's only the ones where they sold.
        var buyerRead = orders.ListDisputedForUserAsync(order.BuyerId, ct);
        var sellerRead = orders.ListDisputedForUserAsync(order.SellerId, ct);
        await Task.WhenAll(buyerRead, sellerRead).ConfigureAwait(false);

        var buyerPast = buyerRead.Result.Where(o => o.Id != order.Id && o.BuyerId == order.BuyerId).ToList();
        var sellerPast = sellerRead.Result.Where(o => o.Id != order.Id && o.SellerId == order.SellerId).ToList();

        return new DisputeHistoryView(
            buyerPast.Count,
            buyerPast.Count(o => o.Dispute!.Resolution == DisputeResolution.ReleaseToSeller),
            sellerPast.Count,
            sellerPast.Count(o => o.Dispute!.Resolution == DisputeResolution.RefundToBuyer));
    }
}

/// <summary>An internal note, or (with <c>to</c>) a request for information that the case waits on and that side is told about.</summary>
public sealed class AddDisputeNoteHandler(
    ICurrentStaff staff,
    IOrderRepository orders,
    IDisputeAdminStore store,
    IIdempotencyStore idempotency,
    IClock clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public Task<ChangeDone> HandleAsync(string orderId, AddDisputeNoteRequest request, string? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return idempotency.RunOnceAsync(
            $"dispute-note:{staff.StaffId}:{orderId}",
            idempotencyKey,
            Retention,
            () => AddAsync(orderId, request, ct),
            ct,
            InFlightPolicy.Reject);
    }

    private async Task<ChangeDone> AddAsync(string orderId, AddDisputeNoteRequest request, CancellationToken ct)
    {
        var order = await DisputeLookup.RequireWithDisputeAsync(orders, orderId, ct).ConfigureAwait(false);
        if (order.EscrowState != EscrowState.Disputed)
        {
            throw DomainException.For("dispute.already_resolved", "This dispute has already been resolved.");
        }

        StaffGuard.EnsureNotSelf(staff.StaffId, order.BuyerId);
        StaffGuard.EnsureNotSelf(staff.StaffId, order.SellerId);

        var to = ParseParty(request.To);
        var note = DisputeNote.Create(
            order.Id, staff.StaffId, staff.StaffName,
            to is null ? DisputeNoteKind.Note : DisputeNoteKind.InfoRequest, to, request.Text, clock);

        OrderNotice[] notices = to is { } side
            ? [OrderNotice.Create(side == DisputeParty.Buyer ? order.BuyerId : order.SellerId, OrderNoticeKind.InfoRequested, order, note.Text, clock)]
            : [];

        var audit = AuditEntry.Create(
            clock, staff.StaffId, staff.StaffName,
            to is null ? AuditActions.DisputeNoteAdded : AuditActions.DisputeInfoRequested,
            AuditTargets.Order, order.Id, staff.RequestId,
            reason: note.Text,
            after: to is { } asked ? $"awaiting information from the {(asked == DisputeParty.Buyer ? "buyer" : "seller")}" : null);

        try
        {
            await store.AddNoteAsync(new DisputeNoteCommit(order, note, to is not null, notices, audit), ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw DomainException.For("dispute.already_resolved", "This dispute has already been resolved.");
        }

        return new ChangeDone();
    }

    private static DisputeParty? ParseParty(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant() switch
            {
                "buyer" => DisputeParty.Buyer,
                "seller" => DisputeParty.Seller,
                _ => throw DomainException.For("admin.note_invalid", "An information request goes to the buyer or the seller."),
            };
}

/// <summary>
/// Rules on a dispute: for the seller (release, with the payout) or for the buyer (a refund of an amount staff decide).
/// </summary>
/// <remarks>
/// <para>
/// The domain does the ruling (<see cref="Order.ResolveDispute"/>); this orchestrates. For the seller it writes the
/// <see cref="Payout"/> row with the released order exactly as <c>EscrowReleaseService</c> does, so the seller is neither owed
/// twice nor owed nothing; the payout sweep sends it (ledger first, transfer second). For the buyer it writes a
/// <see cref="Refund"/> whose ceiling is the order total <i>as read here</i>, then asks <see cref="IRefundSender"/>, which moves no money.
/// </para>
/// <para>
/// Everything (the order, the payout or refund, the decision note, both notices, the audit entry) commits in one DynamoDB
/// transaction, conditional on the order's version, so two staff ruling at once give one winner and
/// <c>dispute.already_resolved</c> for the other. Repeating a request with the same <c>Idempotency-Key</c> changes nothing.
/// </para>
/// </remarks>
public sealed partial class ResolveDisputeHandler(
    ICurrentStaff staff,
    IOrderRepository orders,
    IDisputeAdminStore store,
    IRefundSender refundSender,
    IIdempotencyStore idempotency,
    IClock clock,
    ILogger<ResolveDisputeHandler> logger)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public Task<ChangeDone> HandleAsync(string orderId, ResolveDisputeRequest request, string? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return idempotency.RunOnceAsync(
            $"dispute-resolve:{staff.StaffId}:{orderId}",
            idempotencyKey,
            Retention,
            () => ResolveAsync(orderId, request, ct),
            ct,
            InFlightPolicy.Reject);
    }

    private async Task<ChangeDone> ResolveAsync(string orderId, ResolveDisputeRequest request, CancellationToken ct)
    {
        var order = await DisputeLookup.RequireWithDisputeAsync(orders, orderId, ct).ConfigureAwait(false);
        if (order.EscrowState != EscrowState.Disputed)
        {
            throw DomainException.For("dispute.already_resolved", "This dispute has already been resolved.");
        }

        StaffGuard.EnsureNotSelf(staff.StaffId, order.BuyerId);
        StaffGuard.EnsureNotSelf(staff.StaffId, order.SellerId);

        var reason = AdminReason.Require(request.Reason);
        var forSeller = request.Resolution == DisputeResolution.ReleaseToSeller;

        // A ruling for the seller has no amount; one for the buyer must name one. The ceiling is checked by Refund.Record on this
        // order as read here, never against a number the page sent.
        if (forSeller && request.RefundAmount is not null)
        {
            throw DomainException.For("dispute.refund_amount_invalid", "A ruling for the seller carries no refund.");
        }

        if (!forSeller && request.RefundAmount is null)
        {
            throw DomainException.For("dispute.refund_amount_invalid", "A ruling for the buyer needs a refund amount.");
        }

        // What becomes of the part of the order the refund does not cover is only for a ruling for the buyer.
        if (forSeller && (request.Remainder is not null || request.SellerPayout is not null))
        {
            throw DomainException.For("dispute.remainder_invalid", "A ruling for the seller has no remainder to decide.");
        }

        var remainderChoice = DisputeLookup.ParseRemainder(request.Remainder, request.SellerPayout);

        var expectedVersion = order.Version;
        order.ResolveDispute(request.Resolution, clock);

        var refund = forSeller
            ? null
            : Refund.Record(order, request.RefundAmount!.Value, reason, staff.StaffId, staff.StaffName, clock, remainderChoice);

        // For the seller: the whole payout. For the buyer: the seller's share of the remainder, when staff released or split it.
        var payout = forSeller ? Payout.For(order, clock) : refund!.RemainderPayout(order, clock);

        var decision = DisputeNote.Create(order.Id, staff.StaffId, staff.StaffName, DisputeNoteKind.Decision, null, reason, clock);
        var kind = forSeller ? OrderNoticeKind.DisputeResolvedForSeller : OrderNoticeKind.DisputeResolvedForBuyer;
        OrderNotice[] notices =
        [
            OrderNotice.Create(order.BuyerId, kind, order, reason, clock),
            OrderNotice.Create(order.SellerId, kind, order, reason, clock),
        ];

        var audit = AuditEntry.Create(
            clock, staff.StaffId, staff.StaffName,
            forSeller ? AuditActions.DisputeResolvedForSeller : AuditActions.DisputeResolvedForBuyer,
            AuditTargets.Order, order.Id, staff.RequestId,
            reason: reason,
            before: "Disputed",
            after: forSeller
                ? $"Released to the seller; payout {payout!.Amount} owed"
                : $"Refunded {refund!.Amount} to the buyer, payment pending gateway{DescribeRemainder(refund, payout)}");

        try
        {
            await store.ResolveAsync(new DisputeResolutionCommit(order, expectedVersion, payout, refund, decision, notices, audit), ct)
                .ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            // Lost the race, or the order moved for another reason. Say which: a ruling that already landed is the common case.
            var current = await orders.FindAsync(order.Id, ct).ConfigureAwait(false);
            if (current is null || current.EscrowState != EscrowState.Disputed)
            {
                throw DomainException.For("dispute.already_resolved", "This dispute has already been resolved.");
            }

            throw;
        }

        if (refund is not null)
        {
            await SendRefundAsync(refund, ct).ConfigureAwait(false);
        }

        return new ChangeDone();
    }

    internal static string DescribeRemainder(Refund refund, Payout? payout) => refund.Disposition switch
    {
        RemainderDisposition.Held => $"; remainder {refund.Remainder} held for a later decision",
        RemainderDisposition.ReleasedToSeller => $"; remainder {refund.Remainder} released to the seller, payout {payout!.Amount} owed",
        RemainderDisposition.Split => $"; remainder {refund.Remainder} split, payout {payout!.Amount} owed to the seller",
        _ => string.Empty,
    };

    /// <summary>After the ruling is safely recorded. A sender that fails leaves the refund <c>Recorded</c>; the ruling stands.</summary>
    private async Task SendRefundAsync(Refund refund, CancellationToken ct)
    {
        try
        {
            var outcome = await refundSender.SendAsync(refund, ct).ConfigureAwait(false);
            refund.RecordOutcome(outcome, clock);
            await store.SaveRefundOutcomeAsync(refund, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogSendFailed(logger, refund.OrderNumber, failure);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The refund for order {OrderNumber} was recorded but the sender failed; it stays Recorded.")]
    private static partial void LogSendFailed(ILogger logger, string orderNumber, Exception exception);
}

/// <summary>
/// Decides a remainder that staff held when they refunded the buyer only part of the order: release it to the seller (less the
/// fee) or split it.
/// </summary>
/// <remarks>
/// The money rules are the domain's (<see cref="Refund.SettleRemainder"/>); this orchestrates. The refund moves from held to its
/// settlement and the seller's <see cref="Payout"/> is written in one transaction with the audit entry, conditional on the remainder
/// still being held, so two staff settling at once give one winner and <c>dispute.remainder_not_held</c> for the other. Repeating a
/// request with the same <c>Idempotency-Key</c> changes nothing. The payout is owed like any other: the sweep sends it, and no
/// money moves until a payment gateway exists.
/// </remarks>
public sealed class SettleRemainderHandler(
    ICurrentStaff staff,
    IOrderRepository orders,
    IDisputeAdminStore store,
    IIdempotencyStore idempotency,
    IClock clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public Task<ChangeDone> HandleAsync(string orderId, SettleRemainderRequest request, string? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return idempotency.RunOnceAsync(
            $"dispute-settle:{staff.StaffId}:{orderId}",
            idempotencyKey,
            Retention,
            () => SettleAsync(orderId, request, ct),
            ct,
            InFlightPolicy.Reject);
    }

    private async Task<ChangeDone> SettleAsync(string orderId, SettleRemainderRequest request, CancellationToken ct)
    {
        var order = await DisputeLookup.RequireWithDisputeAsync(orders, orderId, ct).ConfigureAwait(false);
        if (order.EscrowState != EscrowState.Refunded)
        {
            throw NotHeld();
        }

        StaffGuard.EnsureNotSelf(staff.StaffId, order.BuyerId);
        StaffGuard.EnsureNotSelf(staff.StaffId, order.SellerId);

        var reason = AdminReason.Require(request.Reason);
        var choice = DisputeLookup.ParseRemainder(request.Mode, request.SellerPayout)
            ?? throw DomainException.For("dispute.remainder_invalid", "Choose to release the remainder to the seller or to split it.");

        var refund = await store.FindRefundAsync(order.Id, ct).ConfigureAwait(false) ?? throw NotHeld();
        var held = refund.Remainder;
        refund.SettleRemainder(order, choice, clock);
        var payout = refund.RemainderPayout(order, clock)!;

        var audit = AuditEntry.Create(
            clock, staff.StaffId, staff.StaffName, AuditActions.DisputeRemainderSettled, AuditTargets.Order, order.Id, staff.RequestId,
            reason: reason,
            before: $"Remainder {held} held",
            after: refund.Disposition == RemainderDisposition.ReleasedToSeller
                ? $"Released to the seller; payout {payout.Amount} owed"
                : $"Split; payout {payout.Amount} owed to the seller, the platform keeps {held - payout.Amount}");

        try
        {
            await store.SettleRemainderAsync(new RemainderSettlementCommit(order, refund, payout, audit), ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw NotHeld();
        }

        return new ChangeDone();
    }

    private static DomainException NotHeld() =>
        DomainException.For("dispute.remainder_not_held", "This refund has no held remainder to settle.");
}

/// <summary>
/// The overview page. <b>Milestone 1: only the dispute fields are real.</b> Reports, appeals, ID checks, payouts, listings and
/// the money figures are zero or empty until the milestones that own them; the page copes (it shows an all-clear for the queues).
/// </summary>
public sealed class GetOverviewHandler(IDisputeAdminStore store)
{
    public async Task<OverviewViewDto> HandleAsync(CancellationToken ct)
    {
        var open = await store.ListOpenAsync(DisputeLookup.MaxOpen, ct).ConfigureAwait(false);

        return new OverviewViewDto(
            OpenDisputes: open.Count,
            OpenReports: 0,
            SeriousReports: 0,
            OpenAppeals: 0,
            PendingIdChecks: 0,
            FailedPayouts: 0,
            PendingPayouts: 0,
            ActiveListings: 0,
            Daily: [],
            Last30: new Last30View(0, 0, 0, 0),
            Previous30: new Previous30View(0, 0, 0),
            DisputeOpenedAt: [.. open.Select(o => o.Dispute!.OpenedAt)]);
    }
}
