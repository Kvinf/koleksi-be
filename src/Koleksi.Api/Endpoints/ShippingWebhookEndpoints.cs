using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;

namespace Koleksi.Api.Endpoints;

/// <summary>A courier-tracking provider's callback: "this parcel moved" / "this parcel was delivered".</summary>
/// <remarks>
/// Anonymous by necessity (the provider is not a signed-in user) and authenticated instead by the provider's
/// own adapter verifying the raw body's signature, exactly as the payment webhook is. A delivery notice starts the
/// clock that pays the seller, so an unverified body changes nothing. See <c>docs/delivery-providers.md</c>.
/// </remarks>
internal static class ShippingWebhookEndpoints
{
    public static IEndpointRouteBuilder MapShippingWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/shipping/webhook/{provider}", HandleWebhookAsync)
            .WithTags("Shipping")
            .WithName("ShippingWebhook")
            .AllowAnonymous();

        return app;
    }

    /// <summary>Largest body read; a real notice is a few hundred bytes. Bounded before the signature is looked at.</summary>
    internal const int MaxWebhookBodyBytes = 64 * 1024;

    private static async Task<IResult> HandleWebhookAsync(
        string provider, HttpRequest http, ApplyDeliveryNotificationHandler handler, CancellationToken ct)
    {
        var tooLarge = Results.Json(
            new { code = "shipping.payload_too_large" }, statusCode: StatusCodes.Status413PayloadTooLarge);

        if (http.ContentLength > MaxWebhookBodyBytes)
        {
            return tooLarge;
        }

        // Raw text, before anything parses it: the signature covers the exact bytes sent. The read is bounded too,
        // because a chunked body carries no Content-Length to check.
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

        DeliveryWebhookResult result;
        try
        {
            result = await handler.HandleAsync(provider, rawBody, headers, ct).ConfigureAwait(false);
        }
        catch (DeliveryVerificationUnavailableException)
        {
            // The key could not be read. The call may be genuine; a 5xx makes the provider send it again.
            http.HttpContext.Response.Headers.RetryAfter = "30";
            return Results.Json(
                new { code = "shipping.verification_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return result.Outcome switch
        {
            DeliveryWebhookOutcome.Accepted => Results.Ok(),

            // The same answer for "no such provider here" and "bad signature", so the route cannot be used to
            // find out which providers this stage has wired.
            DeliveryWebhookOutcome.Unverified or DeliveryWebhookOutcome.UnknownProvider =>
                Results.Json(new { code = "shipping.unverified" }, statusCode: StatusCodes.Status401Unauthorized),

            DeliveryWebhookOutcome.NotShippedYet =>
                Results.Json(new { code = "shipping.not_shipped_yet" }, statusCode: StatusCodes.Status409Conflict),

            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
