using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Cases;
using Koleksi.Application.Features.Orders;
using Koleksi.Domain.Orders;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// The caller's own money entries: what they are owed as a seller after an order's escrow released, and the refunds they were
/// granted as a buyer after a dispute ruling.
/// </summary>
/// <remarks>
/// <para>
/// No <c>AllowAnonymous</c> and no id in the route: the list is always the caller's, read through the seller index on their own
/// account (payouts) and their own buyer orders (refunds), so there is nothing to forge. Only what a person needs to read is
/// returned: the order number, the amounts, the status and, while one is waiting, a short coded reason. The provider's own
/// reference and any internal error text stay on the server, and so does the staff member who decided a refund.
/// </para>
/// <para>
/// Each entry has a <c>type</c>: <c>sold</c> (a payout to the seller) or <c>refunded</c> (a refund to the buyer). A refund is a
/// record, not a payment: there is no payment gateway, so its status is <c>recordedPendingGateway</c> (or <c>simulated</c> on a
/// development stage) and never means the money arrived. Refunds come with the first page only; later pages (a cursor) are the
/// seller's older payouts.
/// </para>
/// </remarks>
internal static class PayoutEndpoints
{
    public static IEndpointRouteBuilder MapPayoutEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/payouts", ListMyPayoutsAsync).WithTags("Payouts").WithName("ListMyPayouts");

        return app;
    }

    private static async Task<IResult> ListMyPayoutsAsync(
        ICurrentUser user, IPayoutRepository payouts, ListMyRefundsHandler refunds, string? cursor, CancellationToken ct)
    {
        var callerId = user.RequireUserId();
        var page = await payouts.ListForSellerAsync(callerId, cursor, limit: 20, ct).ConfigureAwait(false);

        var entries = page.Items
            .Select(payout => (At: payout.PaidAt ?? payout.CreatedAt, Entry: (object)new
            {
                type = "sold",
                payout.OrderNumber,
                amountRupiah = payout.Amount.Rupiah,
                feeRupiah = payout.Fee.Rupiah,
                status = payout.Status.ToString(),
                payout.CreatedAt,
                payout.PaidAt,

                // A reason only while it is waiting, and only the codes the seller can act on or understand.
                waitingFor = payout.Status == PayoutStatus.Pending ? WaitingFor(payout.LastError) : null,
            }))
            .ToList();

        var refundsUnavailable = false;
        if (cursor is null)
        {
            var refundsView = await refunds.HandleAsync(ct).ConfigureAwait(false);
            refundsUnavailable = refundsView.Unavailable;
            foreach (var refund in refundsView.Items)
            {
                entries.Add((refund.CreatedAt, new
                {
                    type = "refunded",
                    refund.OrderNumber,
                    amountRupiah = refund.AmountRupiah,
                    orderTotalRupiah = refund.OrderTotalRupiah,
                    status = refund.Status,
                    refund.Reason,
                    refund.CreatedAt,
                }));
            }
        }

        return Results.Ok(new
        {
            items = entries.OrderByDescending(entry => entry.At).Select(entry => entry.Entry),
            nextCursor = page.NextCursor,

            // True when the buyer-side refund lookup failed: the list is then payouts only, and the page says so.
            refundsUnavailable,
        });
    }

    private static string? WaitingFor(string? lastError) => lastError switch
    {
        PayoutSender.MissingBankAccount => "bank_account",
        null or "" => null,
        _ => "provider",
    };
}
