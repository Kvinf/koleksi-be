using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;

namespace Koleksi.Domain.Orders;

/// <summary>Which side of an order a staff member is talking to.</summary>
public enum DisputeParty
{
    Buyer = 0,
    Seller = 1,
}

/// <summary>What a case note is.</summary>
public enum DisputeNoteKind
{
    /// <summary>Internal: staff only, never shown to either side.</summary>
    Note = 0,

    /// <summary>A request for more information, addressed to one side and shown to them.</summary>
    InfoRequest = 1,

    /// <summary>The ruling's reason, recorded when the case is resolved and shown to both sides.</summary>
    Decision = 2,

    /// <summary>
    /// A buyer's or seller's answer to the team's request for information. Shown to staff, and to the side that wrote it; the
    /// other side never sees it.
    /// </summary>
    Reply = 3,
}

/// <summary>One entry in a dispute's case file: written by staff, or (<see cref="DisputeNoteKind.Reply"/>) by a buyer or seller.</summary>
public sealed record DisputeNote(
    string Id,
    string OrderId,
    DateTimeOffset At,
    string StaffId,
    string StaffName,
    DisputeNoteKind Kind,
    DisputeParty? To,
    string Text)
{
    /// <summary>Builds a note, validating the text (<c>admin.reason_invalid</c>) and that a request names who it is for.</summary>
    public static DisputeNote Create(
        string orderId, string staffId, string staffName, DisputeNoteKind kind, DisputeParty? to, string? text, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(staffId);
        ArgumentException.ThrowIfNullOrWhiteSpace(staffName);
        ArgumentNullException.ThrowIfNull(clock);

        if ((kind == DisputeNoteKind.InfoRequest) != to.HasValue)
        {
            throw DomainException.For("admin.note_invalid", "An information request names who it is for, and nothing else does.");
        }

        if (kind == DisputeNoteKind.Reply)
        {
            throw DomainException.For("admin.note_invalid", "A reply is written by a buyer or seller, not by staff.");
        }

        var now = clock.UtcNow;
        return new DisputeNote(EntityId.New(now), orderId, now, staffId, staffName.Trim(), kind, to, AdminReason.Require(text));
    }

    /// <summary>The longest reply. The same bound as a complaint (<see cref="Dispute.MaxReasonLength"/>).</summary>
    public const int MaxReplyLength = Dispute.MaxReasonLength;

    /// <summary>
    /// A buyer's or seller's answer. <see cref="StaffId"/> holds the author's account id (so a reply can be tied to who wrote it)
    /// and <see cref="StaffName"/> the side's name, <c>buyer</c> or <c>seller</c>; <see cref="To"/> is the side that wrote it.
    /// </summary>
    /// <exception cref="DomainException"><c>case.reply_invalid</c> when the text is empty or longer than <see cref="MaxReplyLength"/>.</exception>
    public static DisputeNote CreateReply(string orderId, string authorId, DisputeParty side, string? text, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorId);
        ArgumentNullException.ThrowIfNull(clock);

        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxReplyLength || trimmed.Contains('\0', StringComparison.Ordinal))
        {
            throw DomainException.For("case.reply_invalid", $"A reply must be 1 to {MaxReplyLength} characters.");
        }

        var now = clock.UtcNow;
        return new DisputeNote(
            EntityId.New(now), orderId, now, authorId, side == DisputeParty.Buyer ? "buyer" : "seller", DisputeNoteKind.Reply, side, trimmed);
    }
}

/// <summary>What a notice tells its reader.</summary>
public enum OrderNoticeKind
{
    InfoRequested = 0,
    DisputeResolvedForBuyer = 1,
    DisputeResolvedForSeller = 2,
}

/// <summary>
/// Something a person is owed a message about, kept as a row until a delivery path exists.
/// </summary>
/// <remarks>
/// The public site has no stored notifications: its only push is a best-effort WebSocket signal that is gone if nobody is
/// connected. So a decision is recorded here, <b>pending</b>, for each side, in the same transaction as the decision itself,
/// and nothing delivers it yet. Whoever builds notifications reads these rows; until then both sides see the ruling on the
/// order page only if the public site is changed to show it. This is the honest state, not a notification system.
/// </remarks>
public sealed record OrderNotice(
    string Id,
    string UserId,
    OrderNoticeKind Kind,
    string OrderId,
    string OrderNumber,
    string Text,
    DateTimeOffset CreatedAt)
{
    public static OrderNotice Create(string userId, OrderNoticeKind kind, Order order, string text, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.UtcNow;
        return new OrderNotice(EntityId.New(now), userId, kind, order.Id, order.OrderNumber, AdminReason.Require(text), now);
    }
}
