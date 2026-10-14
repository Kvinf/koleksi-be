using System.Globalization;
using System.Text.Json;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure.Payments;

namespace Koleksi.Infrastructure.Delivery;

/// <summary>Where a webhook's signing key comes from. Secrets Manager in a deployed stage; a fixed key in a test.</summary>
internal interface IDeliveryWebhookSecret
{
    /// <exception cref="DeliveryVerificationUnavailableException">The key could not be read.</exception>
    Task<byte[]> GetAsync(CancellationToken ct);
}

/// <summary>
/// The neutral webhook shape every provider adapter is written against: one JSON object, signed with
/// HMAC-SHA256 over the raw body, the signature base64 in <c>X-Koleksi-Signature</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>{ "eventId": "evt_1", "orderNumber": "KLK-AB12CD34", "status": "delivered" }</c>, where status is
/// <c>in_transit</c> or <c>delivered</c> (anything else, such as a pickup or a failed attempt, is read and ignored).
/// A provider whose own webhook looks different gets its own small class implementing
/// <see cref="IDeliveryWebhookProvider"/> (its signature scheme, its field names, its status codes mapped to
/// <see cref="DeliveryState"/>) and a line in <c>AddDeliveryProvider</c>; nothing else in the system changes.
/// </para>
/// <para>
/// It also serves as the contract a provider's webhook can be forwarded into through a signing relay, and as
/// the reference the adapter tests are copied from. Verification reuses the payment webhook's constant-time
/// comparison, over the exact bytes received.
/// </para>
/// </remarks>
internal sealed class SignedJsonDeliveryWebhookProvider(IDeliveryWebhookSecret secret) : IDeliveryWebhookProvider
{
    public const string ProviderName = "signed-json";

    public const string SignatureHeader = "X-Koleksi-Signature";

    /// <summary>Longest event id or order number accepted. Real ones are a few dozen characters.</summary>
    private const int MaxFieldLength = 128;

    public string Name => ProviderName;

    public async Task<DeliveryNotification?> VerifyAndParseAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(headers);

        headers.TryGetValue(SignatureHeader, out var signature);

        // A request with no signature is refused before the key is fetched: nothing to compare, nothing to spend.
        if (string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }

        var key = await secret.GetAsync(ct).ConfigureAwait(false);
        if (!PaymentWebhookVerifier.IsAuthentic(rawBody, signature, key))
        {
            return null;
        }

        return Parse(rawBody);
    }

    internal static DeliveryNotification? Parse(string rawBody)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var eventId = Text(root, "eventId");
            var orderNumber = Text(root, "orderNumber");
            var status = Text(root, "status");
            if (eventId is null || orderNumber is null || status is null)
            {
                return null;
            }

            var state = status.ToLower(CultureInfo.InvariantCulture) switch
            {
                "delivered" => DeliveryState.Delivered,
                "in_transit" => DeliveryState.InTransit,
                _ => DeliveryState.Unknown,
            };

            return new DeliveryNotification(eventId, orderNumber, state);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > MaxFieldLength ? null : text;
    }
}
