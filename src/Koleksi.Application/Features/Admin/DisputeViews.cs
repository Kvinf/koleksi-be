namespace Koleksi.Application.Features.Admin;

// The JSON the admin site reads. These records are the contract in koleksi-admin/app/lib/backoffice.types.ts: same names
// (camelCase on the wire), money in whole rupiah, times ISO 8601 UTC. A value the page's types write as a string union is a
// string here, with exactly those spellings, rather than an enum whose names would not match.

public sealed record PartyRefView(string UserId, string Handle, string DisplayName);

public sealed record DisputeSummaryView(
    string OrderId,
    string OrderNumber,
    PartyRefView Buyer,
    PartyRefView Seller,
    string ItemTitle,
    long Total,
    DateTimeOffset OpenedAt,
    string ReasonExcerpt,
    string Status,
    string? Resolution,
    DateTimeOffset? ResolvedAt);

public sealed record OrderLineViewDto(string ListingId, string Title, long UnitPrice, int Quantity);

public sealed record CaseNoteView(string Id, DateTimeOffset At, string StaffName, string Kind, string? To, string Text);

public sealed record ChatLineView(DateTimeOffset At, string From, string Text);

/// <param name="Remainder">The order total less the refund. Zero for a full refund (and for a refund recorded before remainders existed).</param>
/// <param name="RemainderStatus"><c>none</c>, <c>held</c>, <c>releasedToSeller</c> or <c>split</c>.</param>
/// <param name="SellerPayout">What the seller is owed out of the remainder. Zero unless released or split.</param>
/// <param name="SettledAt">When a held remainder was decided.</param>
public sealed record RefundViewDto(
    long Amount, string Reason, DateTimeOffset At, string Status, string StaffName,
    long Remainder, string RemainderStatus, long SellerPayout, DateTimeOffset? SettledAt);

public sealed record CourierView(string Code, string Service);

public sealed record ProofView(bool ParcelPhoto, bool LabelPhoto);

public sealed record DisputeHistoryView(int BuyerDisputes, int BuyerLost, int SellerDisputes, int SellerLost);

/// <summary>A <see cref="DisputeSummaryView"/> plus everything the case page shows. Same property names, flattened, as the page's <c>DisputeCase</c>.</summary>
public sealed record DisputeCaseView(
    string OrderId,
    string OrderNumber,
    PartyRefView Buyer,
    PartyRefView Seller,
    string ItemTitle,
    long Total,
    DateTimeOffset OpenedAt,
    string ReasonExcerpt,
    string Status,
    string? Resolution,
    DateTimeOffset? ResolvedAt,
    string Reason,
    IReadOnlyList<OrderLineViewDto> Lines,
    long ItemsTotal,
    long ShippingCost,
    decimal FeePercent,
    long PlatformFee,
    long SellerPayout,
    string EscrowState,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    CourierView Courier,
    string? TrackingNumber,
    ProofView Proof,
    string ShipToCity,
    IReadOnlyList<ChatLineView> Chat,
    IReadOnlyList<CaseNoteView> Notes,
    RefundViewDto? Refund,
    DisputeHistoryView History);

public sealed record DailyPointView(string Date, long Gmv, int Orders);

public sealed record Last30View(long Gmv, int Orders, long Fees, long Refunds);

public sealed record Previous30View(long Gmv, int Orders, long Fees);

/// <summary>
/// The overview. <b>Only the dispute fields are real in Milestone 1</b>; everything else is zero or empty until the milestone
/// that owns it (reports 2, ID checks and payouts 3, money 4) fills it in.
/// </summary>
public sealed record OverviewViewDto(
    int OpenDisputes,
    int OpenReports,
    int SeriousReports,
    int OpenAppeals,
    int PendingIdChecks,
    int FailedPayouts,
    int PendingPayouts,
    int ActiveListings,
    IReadOnlyList<DailyPointView> Daily,
    Last30View Last30,
    Previous30View Previous30,
    IReadOnlyList<DateTimeOffset> DisputeOpenedAt);

public sealed record HolidayView(string Date, string Name, string Kind);
