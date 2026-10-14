using Koleksi.Application.Abstractions;
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

    /// <summary>
    /// Largest webhook body read. A real notification is a few hundred bytes; the cap exists so an
    /// unauthenticated caller cannot make this endpoint buffer and hash an arbitrarily large body
    /// before the signature is even looked at.
    /// </summary>
    internal const int MaxWebhookBodyBytes = 64 * 1024;

    private static async Task<IResult> HandleWebhookAsync(
        HttpRequest http, ConfirmOrderPaymentHandler handler, CancellationToken ct)
    {
        var tooLarge = Results.Json(
            new { code = "payment.payload_too_large" }, statusCode: StatusCodes.Status413PayloadTooLarge);

        if (http.ContentLength > MaxWebhookBodyBytes)
        {
            return tooLarge;
        }

        // Read as raw text before anything parses it. The signature covers the exact
        // bytes the gateway sent; re-serialised JSON would not reproduce them byte for
        // byte and would silently fail every genuine notification. The read itself is bounded
        // too: a chunked body carries no Content-Length to check up front.
        var buffer = new byte[MaxWebhookBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await http.Body.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaxWebhookBodyBytes)
        {
            return tooLarge;
        }

        using var reader = new StreamReader(new MemoryStream(buffer, 0, total));
        var rawBody = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        var headers = http.Headers.ToDictionary(
            header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        ConfirmOrderPaymentResult result;
        try
        {
            result = await handler.HandleAsync(rawBody, headers, ct).ConfigureAwait(false);
        }
        catch (PaymentVerificationUnavailableException)
        {
            // The signing key could not be read (already logged at error level with the cause). 503 with
            // a retry hint, not a bare 500: the notification may be perfectly genuine, and a 5xx is what
            // makes the gateway deliver it again once the key is readable.
            http.HttpContext.Response.Headers.RetryAfter = "30";
            return Results.Json(
                new { code = "payment.verification_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

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
