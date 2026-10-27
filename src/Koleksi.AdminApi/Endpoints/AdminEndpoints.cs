using Koleksi.AdminApi.Security;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Admin;
using Koleksi.Domain.Admin;
using Microsoft.AspNetCore.Mvc;

namespace Koleksi.AdminApi.Endpoints;

/// <summary>The admin API's routes. Every one needs a staff token and names the capability it needs.</summary>
internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/admin");

        // The web tier calls this once after a staff member signs in. It writes the first audit entry and
        // tells the web tier who the API believes the caller is.
        group.MapPost("/session", async (
                HttpRequest request,
                StartStaffSessionHandler handler,
                CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(IdempotencyHeader.Read(request), ct).ConfigureAwait(false)))
            .RequireAuthorization(AdminCapabilities.SessionStart)
            .RequireRateLimiting(AdminRateLimits.Write);

        group.MapGet("/audit", async (
                [FromQuery] string? actor,
                [FromQuery] string? targetType,
                [FromQuery] string? targetId,
                [FromQuery] string? cursor,
                [FromQuery] int? limit,
                ListAuditHandler handler,
                CancellationToken ct) =>
            {
                var page = await handler
                    .HandleAsync(new AuditQuery(actor, targetType, targetId, cursor, limit ?? AuditQuery.DefaultLimit), ct)
                    .ConfigureAwait(false);
                return Results.Ok(AuditPageResponse.From(page));
            })
            .RequireAuthorization(AdminCapabilities.AuditRead);

        return app;
    }
}

/// <summary>The wire shape of one audit entry. Times are ISO-8601 UTC.</summary>
internal sealed record AuditEntryResponse(
    string Id,
    DateTimeOffset At,
    string ActorId,
    string ActorName,
    string Action,
    string TargetType,
    string TargetId,
    string? Reason,
    string? Before,
    string? After,
    string RequestId)
{
    public static AuditEntryResponse From(AuditEntry entry) =>
        new(entry.Id, entry.At, entry.ActorId, entry.ActorName, entry.Action, entry.TargetType, entry.TargetId,
            entry.Reason, entry.Before, entry.After, entry.RequestId);
}

internal sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Items, string? NextCursor)
{
    public static AuditPageResponse From(AuditPage page) =>
        new([.. page.Items.Select(AuditEntryResponse.From)], page.NextCursor);
}

/// <summary>Reads the optional client-supplied double-submit key, the same way the public API does.</summary>
internal static class IdempotencyHeader
{
    private const string Name = "Idempotency-Key";

    /// <summary>The header value, or <c>null</c> when absent, blank or implausibly long (a real client sends a UUID).</summary>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = request.Headers[Name].ToString();
        return string.IsNullOrWhiteSpace(value) || value.Length > 128 ? null : value;
    }
}
