using Koleksi.Api.Security;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Sweeps;
using Koleksi.Domain.Common;
using Koleksi.Infrastructure;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// Doors for this system's own machinery, not for any user.
/// </summary>
/// <remarks>
/// <para>
/// <c>POST /internal/sweep</c> is called by an EventBridge rule every few minutes, which invokes
/// this same Lambda with a synthetic HTTP API event (so no new compute exists to run the sweep).
/// The route is also reachable from the internet through the gateway's catch-all, so the shared
/// secret in <c>X-Koleksi-Internal</c> is the only gate, and it is a deliberately quiet one.
/// </para>
/// <para>
/// <b>Why <c>AllowAnonymous</c>, and why 404.</b> The scheduler holds no user token, so the
/// framework's authentication cannot be what protects it. Every refusal is the plain empty 404 an
/// unknown route gets: in this app that is a 401 (the fallback authorization policy challenges
/// every unmatched, unauthenticated request), so a refusal here is a challenge too, and a prober
/// cannot tell this route from any other. That covers no secret configured, no header, a wrong one,
/// or the wrong HTTP method. The method is checked in the handler for that reason; a POST-only
/// route would answer 405 to a GET and give itself away.
/// </para>
/// <para>
/// <b>Not user-rate-limited.</b> It carries no named limiter: it has no user, and its caller is a
/// schedule. The global per-address limiter still bounds anyone guessing at it.
/// </para>
/// <para>
/// <b>Locally</b>, set <c>KOLEKSI__SECURITY__INTERNALSECRET</c> (at least 32 characters) in
/// <c>koleksi-backend/.env</c>, restart the API, and POST to <c>/internal/sweep</c> with the header
/// <c>X-Koleksi-Internal: &lt;that secret&gt;</c> from any HTTP tool.
/// </para>
/// </remarks>
internal static class InternalEndpoints
{
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Map("/internal/sweep", SweepAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.Map("/internal/suggest-backfill", SuggestBackfillAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.Map("/internal/order-sweep-backfill", OrderSweepBackfillAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

#if DEBUG
    /// <summary>
    /// Development only: marks an unpaid order as paid, as the gateway's settled webhook would, so the whole
    /// escrow flow can run on a laptop. <c>POST /internal/dev/pay-order?orderNumber=KLK-...</c>, behind the
    /// internal secret. Never mapped outside the Development environment (see <c>Program.cs</c>).
    /// </summary>
    public static IEndpointRouteBuilder MapDevelopmentPaymentEndpoint(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Map("/internal/dev/pay-order", PayOrderAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.Map("/internal/dev/deliver-order", DeliverOrderAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>
    /// Development only: the courier reporting a shipped parcel delivered, at once, so the buyer's two-day window
    /// can be seen without waiting for the 30-minute courier poll. <c>POST /internal/dev/deliver-order?orderNumber=...</c>.
    /// </summary>
    private static async Task<IResult> DeliverOrderAsync(HttpContext http, SecurityOptions security, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(http.Request.Method)
            || !InternalSecretGate.IsAuthorised(security.InternalSecret, http.Request.Headers[TrustedClientIpMiddleware.SecretHeader]))
        {
            return Results.Challenge();
        }

        var orders = http.RequestServices.GetRequiredService<IOrderRepository>();
        var clock = http.RequestServices.GetRequiredService<IClock>();
        var order = await orders.FindByOrderNumberAsync(http.Request.Query["orderNumber"].ToString(), ct).ConfigureAwait(false);
        if (order is null)
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        var version = order.Version;
        order.MarkDelivered(clock);
        await orders.UpdateAsync(order, version, ct).ConfigureAwait(false);

        return Results.Ok(new { escrowState = order.EscrowState.ToString(), autoReleaseAt = order.AutoReleaseAt });
    }

    private static async Task<IResult> PayOrderAsync(HttpContext http, SecurityOptions security, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(http.Request.Method)
            || !InternalSecretGate.IsAuthorised(security.InternalSecret, http.Request.Headers[TrustedClientIpMiddleware.SecretHeader]))
        {
            return Results.Challenge();
        }

        var number = http.Request.Query["orderNumber"].ToString();
        var orders = http.RequestServices.GetRequiredService<IOrderRepository>();
        var order = await orders.FindByOrderNumberAsync(number, ct).ConfigureAwait(false);
        if (order is null)
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        var handler = http.RequestServices.GetRequiredService<ConfirmOrderPaymentHandler>();
        await handler.ApplyTrustedAsync(
            new PaymentNotification(order.OrderNumber, PaymentStatus.Settled, order.GrandTotal, $"DEV-{order.Id}", $"dev-{order.Id}"),
            ct).ConfigureAwait(false);

        var after = await orders.FindAsync(order.Id, ct).ConfigureAwait(false);
        return Results.Ok(new { orderNumber = number, escrowState = after?.EscrowState.ToString() });
    }
#endif

    /// <summary>
    /// One bounded step of writing search-suggestion rows for listings published before suggestions
    /// existed. Behind the same secret as the sweep and refused the same way. Call it again with the
    /// returned <c>nextCursor</c> (query string <c>cursor</c>) until that is null. Idempotent.
    /// </summary>
    private static async Task<IResult> SuggestBackfillAsync(HttpContext http, SecurityOptions security, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(http.Request.Method)
            || !InternalSecretGate.IsAuthorised(security.InternalSecret, http.Request.Headers[TrustedClientIpMiddleware.SecretHeader]))
        {
            return Results.Challenge();
        }

        const int perStep = 50;
        var index = http.RequestServices.GetRequiredService<IListingSuggestionIndex>();
        var page = await index.BackfillAsync(http.Request.Query["cursor"].ToString(), perStep, ct).ConfigureAwait(false);

        return Results.Ok(new { indexed = page.Indexed, nextCursor = page.NextCursor });
    }

    /// <summary>
    /// One bounded step of giving orders written before the sweep keys existed the keys the unpaid-order
    /// and auto-release sweeps read (QA D-12). Same secret and the same quiet refusal as the sweep. Call it
    /// again with the returned <c>nextCursor</c> (query string <c>cursor</c>) until that is null. Idempotent.
    /// </summary>
    private static async Task<IResult> OrderSweepBackfillAsync(HttpContext http, SecurityOptions security, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(http.Request.Method)
            || !InternalSecretGate.IsAuthorised(security.InternalSecret, http.Request.Headers[TrustedClientIpMiddleware.SecretHeader]))
        {
            return Results.Challenge();
        }

        // Rows READ per step, not rows changed: it is a table scan, so the bound is on the read cost.
        const int perStep = 200;
        var backfill = http.RequestServices.GetRequiredService<IOrderSweepBackfill>();
        var page = await backfill.BackfillAsync(http.Request.Query["cursor"].ToString(), perStep, ct).ConfigureAwait(false);

        return Results.Ok(new { examined = page.Examined, updated = page.Updated, nextCursor = page.NextCursor });
    }

    private static async Task<IResult> SweepAsync(HttpContext http, SecurityOptions security, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(http.Request.Method)
            || !InternalSecretGate.IsAuthorised(security.InternalSecret, http.Request.Headers[TrustedClientIpMiddleware.SecretHeader]))
        {
            // The same answer as a path that does not exist: see the remarks above.
            return Results.Challenge();
        }

        // Resolved only now: a probe that fails the check should cost no more than a 404.
        var sweep = http.RequestServices.GetRequiredService<SweepOrchestrator>();
        return Results.Ok(await sweep.RunAsync(ct).ConfigureAwait(false));
    }
}
