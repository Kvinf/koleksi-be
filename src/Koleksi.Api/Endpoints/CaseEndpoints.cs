using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Cases;
using Koleksi.Domain.Orders;
using Microsoft.AspNetCore.RateLimiting;

using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>A signed-in buyer's or seller's own dispute cases: status, the team's questions to them, and the ruling.</summary>
/// <remarks>
/// No <c>AllowAnonymous</c>. The list has no id in the route: it is always the caller's, built from their own dispute pointers, so there
/// is nothing to forge and no case of anyone else's can be asked for. The reply names an order, but the order must have the
/// caller as its buyer or seller or the answer is the same 404 as for an order that does not exist. The response carries no account id
/// and no staff name; the team's internal notes are not readable from here at all (see <c>ICaseStore</c>).
/// </remarks>
internal static class CaseEndpoints
{
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapCaseEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/cases", ListMyCasesAsync).WithTags("Cases").WithName("ListMyCases");
        app.MapPost("/api/cases/{orderId}/reply", ReplyAsync)
            .RequireRateLimiting(RateLimitPolicies.Messaging)
            .WithTags("Cases")
            .WithName("ReplyToCase");

        return app;
    }

    // Reading is what delivers the team's messages. The security-headers middleware already marks every API answer
    // `Cache-Control: no-store` unless a public endpoint opts out, and this one does not.
    private static async Task<IResult> ListMyCasesAsync(ListMyCasesHandler handler, CancellationToken ct) =>
        Results.Ok(await handler.HandleAsync(ct).ConfigureAwait(false));

    private static async Task<IResult> ReplyAsync(
        string orderId,
        ReplyRequest request,
        ReplyToCaseHandler handler,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Results.BadRequest(new { code = "case.reply_required" });
        }

        if (text.Length > DisputeNote.MaxReplyLength)
        {
            return Results.BadRequest(new { code = "case.reply_too_long" });
        }

        var outcome = await idempotency.RunOnceAsync(
            $"case.reply:{user.RequireUserId()}:{orderId}",
            Read(http),
            IdempotencyRetention,
            () => handler.HandleAsync(orderId, text, ct),
            ct,
            inFlight: InFlightPolicy.Reject).ConfigureAwait(false);

        return outcome.NotFound ? Results.NotFound(new { code = "case.not_found" }) : Results.Ok(new { done = true });
    }

    private sealed record ReplyRequest(string? Text);
}
