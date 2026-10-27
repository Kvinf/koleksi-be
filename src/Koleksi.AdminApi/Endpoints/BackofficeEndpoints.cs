using Koleksi.AdminApi.Security;
using Koleksi.Application.Features.Admin;
using Microsoft.AspNetCore.Mvc;

namespace Koleksi.AdminApi.Endpoints;

/// <summary>
/// The back office's Milestone 1 routes: the overview, disputes, and the holiday list. The paths, bodies and answers are the contract in
/// <c>koleksi-admin/BACKOFFICE.md</c> and <c>backoffice.types.ts</c>. Every route names the capability it needs; every POST is a staff
/// action that writes its audit entry in the same transaction as the change, and honours <c>Idempotency-Key</c>.
/// </summary>
internal static class BackofficeEndpoints
{
    public static IEndpointRouteBuilder MapBackofficeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/admin");

        // Only the dispute fields are real so far; see GetOverviewHandler.
        group.MapGet("/overview", async (GetOverviewHandler handler, CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(ct).ConfigureAwait(false)))
            .RequireAuthorization(AdminCapabilities.OverviewRead);

        group.MapGet("/disputes", async (
                [FromQuery] string? status,
                [FromQuery] string? q,
                ListDisputesHandler handler,
                CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(status ?? "open", q, ct).ConfigureAwait(false)))
            .RequireAuthorization(AdminCapabilities.DisputesRead);

        group.MapGet("/disputes/{orderId}", async (string orderId, GetDisputeHandler handler, CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(orderId, ct).ConfigureAwait(false)))
            .RequireAuthorization(AdminCapabilities.DisputesRead);

        group.MapPost("/disputes/{orderId}/notes", async (
                string orderId,
                [FromBody] AddDisputeNoteRequest body,
                HttpRequest request,
                AddDisputeNoteHandler handler,
                CancellationToken ct) =>
            {
                await handler.HandleAsync(orderId, body, IdempotencyHeader.Read(request), ct).ConfigureAwait(false);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminCapabilities.DisputesNote)
            .RequireRateLimiting(AdminRateLimits.Write);

        group.MapPost("/disputes/{orderId}/resolve", async (
                string orderId,
                [FromBody] ResolveDisputeRequest body,
                HttpRequest request,
                ResolveDisputeHandler handler,
                CancellationToken ct) =>
            {
                await handler.HandleAsync(orderId, body, IdempotencyHeader.Read(request), ct).ConfigureAwait(false);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminCapabilities.DisputesResolve)
            .RequireRateLimiting(AdminRateLimits.Write);

        // Decides a remainder that was held when the buyer was refunded only part of the order. Same capability as ruling.
        group.MapPost("/disputes/{orderId}/settle-remainder", async (
                string orderId,
                [FromBody] SettleRemainderRequest body,
                HttpRequest request,
                SettleRemainderHandler handler,
                CancellationToken ct) =>
            {
                await handler.HandleAsync(orderId, body, IdempotencyHeader.Read(request), ct).ConfigureAwait(false);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminCapabilities.DisputesResolve)
            .RequireRateLimiting(AdminRateLimits.Write);

        group.MapGet("/holidays", async ([FromQuery] int? year, ListHolidaysHandler handler, CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(year, ct).ConfigureAwait(false)))
            .RequireAuthorization(AdminCapabilities.HolidaysRead);

        group.MapPost("/holidays", async (
                [FromBody] AddHolidayRequest body,
                HttpRequest request,
                AddHolidayHandler handler,
                CancellationToken ct) =>
            {
                await handler.HandleAsync(body, IdempotencyHeader.Read(request), ct).ConfigureAwait(false);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminCapabilities.HolidaysEdit)
            .RequireRateLimiting(AdminRateLimits.Write);

        group.MapPost("/holidays/{date}/remove", async (
                string date,
                HttpRequest request,
                RemoveHolidayHandler handler,
                CancellationToken ct) =>
            {
                await handler.HandleAsync(date, IdempotencyHeader.Read(request), ct).ConfigureAwait(false);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminCapabilities.HolidaysEdit)
            .RequireRateLimiting(AdminRateLimits.Write);

        return app;
    }
}
