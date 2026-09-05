using System.Security.Cryptography;
using System.Text;
using Koleksi.Infrastructure.Payments;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// Webhook signature verification.
/// </summary>
/// <remarks>
/// The most security-critical function in the backend. A forged webhook that verifies is
/// a free order: it moves an escrow order from awaiting-payment to paid without any money
/// having moved, and the seller ships against it.
/// </remarks>
public sealed class PaymentWebhookVerifierTests
{
    private static readonly byte[] Secret = "a-shared-key-from-secrets-manager"u8.ToArray();

    private const string Body =
        """{"event":"payment.settled","reference":"KLK-7F3A21","amount":2500000}""";

    [Fact]
    public void A_body_signed_with_the_shared_key_verifies()
    {
        PaymentWebhookVerifier.IsAuthentic(Body, Sign(Body, Secret), Secret).ShouldBeTrue();
    }

    [Fact]
    public void A_body_signed_with_a_different_key_does_not()
    {
        var attackersKey = "a-key-the-attacker-chose-themselves"u8.ToArray();

        PaymentWebhookVerifier.IsAuthentic(Body, Sign(Body, attackersKey), Secret).ShouldBeFalse();
    }

    [Fact]
    public void A_body_altered_after_signing_does_not()
    {
        // The realistic attack is not a forged signature but a replayed one over an
        // amended body — the same notification with a larger settled amount.
        var signature = Sign(Body, Secret);
        var tampered = Body.Replace("2500000", "25000000", StringComparison.Ordinal);

        PaymentWebhookVerifier.IsAuthentic(tampered, signature, Secret).ShouldBeFalse();
    }

    [Fact]
    public void A_single_flipped_byte_does_not()
    {
        var signature = Convert.FromBase64String(Sign(Body, Secret));
        signature[0] ^= 0x01;

        PaymentWebhookVerifier
            .IsAuthentic(Body, Convert.ToBase64String(signature), Secret)
            .ShouldBeFalse();
    }

    [Fact]
    public void A_truncated_signature_does_not()
    {
        // Length mismatch has to be answered without an early return, or the response
        // time reveals how long the real signature is.
        var signature = Convert.FromBase64String(Sign(Body, Secret));

        PaymentWebhookVerifier
            .IsAuthentic(Body, Convert.ToBase64String(signature.AsSpan(0, 16).ToArray()), Secret)
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not base64 at all")]
    [InlineData("=")]
    public void A_missing_or_malformed_signature_header_does_not(string? header)
    {
        // No exception either. An unhandled FormatException here would be a 500 on an
        // endpoint anyone on the internet can post to.
        PaymentWebhookVerifier.IsAuthentic(Body, header, Secret).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_body_signed_correctly_still_verifies()
    {
        // Nothing about an empty body is special to the signature. Rejecting it here would
        // be a rule invented in the wrong place; whether an empty notification means
        // anything is the parser's decision.
        PaymentWebhookVerifier.IsAuthentic(string.Empty, Sign(string.Empty, Secret), Secret)
            .ShouldBeTrue();
    }

    [Fact]
    public void The_signature_covers_the_body_exactly_as_received()
    {
        // Re-serialising parsed JSON changes whitespace and key order and never matches.
        // This is the mistake that presents as "webhooks stopped working in production".
        var reserialised = Body.Replace(",", ", ", StringComparison.Ordinal);

        PaymentWebhookVerifier.IsAuthentic(reserialised, Sign(Body, Secret), Secret).ShouldBeFalse();
    }

    private static string Sign(string body, byte[] key) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body)));
}
