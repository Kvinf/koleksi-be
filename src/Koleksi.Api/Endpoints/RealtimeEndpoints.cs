using Koleksi.Application.Abstractions;
using Microsoft.AspNetCore.RateLimiting;

namespace Koleksi.Api.Endpoints;

/// <summary>Setting up a live-updates connection for the signed-in caller.</summary>
/// <remarks>
/// <para>
/// The only route here mints a ticket, and it needs a valid token like everything else (no
/// <c>AllowAnonymous</c>). The frontend's server calls it on the user's behalf and hands the
/// ticket to the browser, which presents it once when it opens its WebSocket — see
/// <see cref="IRealtimeTicketStore"/> for why a ticket and not the token.
/// </para>
/// <para>
/// Signed-out visitors need no ticket: they connect anonymously and can subscribe only to
/// public auction topics. A ticket adds exactly one thing, the caller's own private channel.
/// </para>
/// </remarks>
internal static class RealtimeEndpoints
{
    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/realtime/tickets", IssueTicketAsync)
            .RequireRateLimiting(RateLimitPolicies.Realtime)
            .WithTags("Realtime")
            .WithName("IssueRealtimeTicket");

        return app;
    }

    private static async Task<IResult> IssueTicketAsync(
        ICurrentUser currentUser, IRealtimeTicketStore tickets, CancellationToken ct)
    {
        var ticket = await tickets.IssueAsync(currentUser.RequireUserId(), ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            ticket,
            expiresInSeconds = (int)IRealtimeTicketStore.Lifetime.TotalSeconds,
        });
    }
}
