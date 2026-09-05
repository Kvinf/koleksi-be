using System.Security.Cryptography;
using System.Text;

namespace Koleksi.Infrastructure.Payments;

/// <summary>
/// Verifies that a webhook body really came from the payment processor.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most security-critical function in the backend. A forged webhook
/// that verifies is a free order: it moves an escrow order from awaiting-payment to paid
/// without any money having moved, and the seller ships against it.
/// </para>
/// <para>
/// It is deliberately a small, dependency-free, separately testable type rather than a
/// method on the gateway client. It has no network access, no configuration lookup, and no
/// logging — nothing that could be mocked away in a test that then proves nothing.
/// </para>
/// </remarks>
internal static class PaymentWebhookVerifier
{
    /// <summary>
    /// Compares a received signature against the one the body should produce.
    /// </summary>
    /// <param name="rawBody">
    /// The body exactly as received, before any parsing. Re-serialising the parsed JSON
    /// would change whitespace and key order and never match.
    /// </param>
    /// <param name="signatureHeader">The signature the processor sent, base64-encoded.</param>
    /// <param name="secret">The shared key, from Secrets Manager.</param>
    public static bool IsAuthentic(string rawBody, string? signatureHeader, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(secret);

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        byte[] received;
        try
        {
            received = Convert.FromBase64String(signatureHeader);
        }
        catch (FormatException)
        {
            // Not even base64. Nothing to compare, and nothing worth logging the value of.
            return false;
        }

        var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(rawBody));

        // Constant time. A byte-by-byte comparison that returns on the first mismatch
        // leaks, through response timing, how many leading bytes of a guessed signature
        // were correct — which is enough to forge one in a few thousand requests.
        // FixedTimeEquals also handles the length mismatch without an early return.
        return CryptographicOperations.FixedTimeEquals(received, expected);
    }
}
