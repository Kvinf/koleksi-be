using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
using Koleksi.Domain.Orders;

namespace Koleksi.Api.Endpoints;

/// <summary>A seller's own payouts: what they are owed after an order's escrow released, and whether it has been sent.</summary>
/// <remarks>
/// No <c>AllowAnonymous</c> and no id in the route: the list is always the caller's, read through the seller
/// index on their own account, so there is nothing to forge. Only what a seller needs to read is returned: the
/// order number, the amounts, the status and, while one is waiting, a short coded reason. The provider's own
/// reference and any internal error text stay on the server.
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
        ICurrentUser user, IPayoutRepository payouts, string? cursor, CancellationToken ct)
    {
        var page = await payouts.ListForSellerAsync(user.RequireUserId(), cursor, limit: 20, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            items = page.Items.Select(payout => new
            {
                payout.OrderNumber,
                amountRupiah = payout.Amount.Rupiah,
                feeRupiah = payout.Fee.Rupiah,
                status = payout.Status.ToString(),
                payout.CreatedAt,
                payout.PaidAt,

                // A reason only while it is waiting, and only the codes the seller can act on or understand.
                waitingFor = payout.Status == PayoutStatus.Pending ? WaitingFor(payout.LastError) : null,
            }),
            nextCursor = page.NextCursor,
        });
    }

    private static string? WaitingFor(string? lastError) => lastError switch
    {
        PayoutSender.MissingBankAccount => "bank_account",
        null or "" => null,
        _ => "provider",
    };
}
