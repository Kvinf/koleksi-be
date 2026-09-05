using Koleksi.Application.Features.Orders;

namespace Koleksi.Api.Endpoints;

/// <summary>The payment gateway's callback.</summary>
/// <remarks>
/// Anonymous by necessity — the gateway is not a signed-in user and carries no bearer
/// token — and authenticated instead by <see cref="ConfirmOrderPaymentHandler"/> verifying
/// the raw body's HMAC signature. See <c>docs/security.md</c>'s payment-webhooks section
/// for why this is the one place in the product an unauthenticated write is correct.
/// </remarks>
internal static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/payments/webhook", HandleWebhookAsync)
            .WithTags("Payments")
            .WithName("PaymentWebhook")
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> HandleWebhookAsync(
        HttpRequest http, ConfirmOrderPaymentHandler handler, CancellationToken ct)
    {
        // Read as raw text before anything parses it. The signature covers the exact
        // bytes the gateway sent; re-serialised JSON would not reproduce them byte for
        // byte and would silently fail every genuine notification.
        using var reader = new StreamReader(http.Body);
        var rawBody = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        var headers = http.Headers.ToDictionary(
            header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var result = await handler.HandleAsync(rawBody, headers, ct).ConfigureAwait(false);

        if (!result.Verified)
        {
            return Results.Json(new { code = "payment.unverified" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        // 200 regardless of whether the reference matched an order. A notification for an
        // order that does not exist (or already resolved) is not something the gateway
        // should ever retry — retrying would just repeat the same no-op.
        return Results.Ok();
    }
}
