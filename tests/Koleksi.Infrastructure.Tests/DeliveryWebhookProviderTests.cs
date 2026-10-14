using System.Security.Cryptography;
using System.Text;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure.Delivery;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// The neutral signed-JSON webhook every provider adapter is modelled on: only a body signed with the shared key
/// is believed, and only the two states that matter are acted on.
/// </summary>
public sealed class DeliveryWebhookProviderTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("a-shared-delivery-webhook-key-123");

    private sealed class FixedSecret(byte[]? key) : IDeliveryWebhookSecret
    {
        public int Reads { get; private set; }

        public Task<byte[]> GetAsync(CancellationToken ct)
        {
            Reads++;
            return key is null
                ? throw new DeliveryVerificationUnavailableException("no key")
                : Task.FromResult(key);
        }
    }

    private static string Sign(string body, byte[]? key = null) =>
        Convert.ToBase64String(HMACSHA256.HashData(key ?? Key, Encoding.UTF8.GetBytes(body)));

    private static Dictionary<string, string> Headers(string? signature)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (signature is not null)
        {
            headers[SignedJsonDeliveryWebhookProvider.SignatureHeader] = signature;
        }

        return headers;
    }

    private static string Body(string status = "delivered", string orderNumber = "KLK-AB12CD34", string eventId = "evt_1") =>
        "{\"eventId\":\"" + eventId + "\",\"orderNumber\":\"" + orderNumber + "\",\"status\":\"" + status + "\"}";

    private static SignedJsonDeliveryWebhookProvider Provider(FixedSecret? secret = null) => new(secret ?? new FixedSecret(Key));

    [Fact]
    public async Task A_correctly_signed_delivered_body_is_read()
    {
        var body = Body();

        var notification = await Provider().VerifyAndParseAsync(body, Headers(Sign(body)), CancellationToken.None);

        notification.ShouldNotBeNull();
        notification.EventId.ShouldBe("evt_1");
        notification.OrderNumber.ShouldBe("KLK-AB12CD34");
        notification.State.ShouldBe(DeliveryState.Delivered);
    }

    [Theory]
    [InlineData("in_transit", DeliveryState.InTransit)]
    [InlineData("IN_TRANSIT", DeliveryState.InTransit)]
    [InlineData("pickup_failed", DeliveryState.Unknown)]
    public async Task Statuses_map_to_our_states_and_anything_else_is_unknown(string status, DeliveryState expected)
    {
        var body = Body(status);

        var notification = await Provider().VerifyAndParseAsync(body, Headers(Sign(body)), CancellationToken.None);

        notification.ShouldNotBeNull();
        notification.State.ShouldBe(expected);
    }

    [Fact]
    public async Task A_body_changed_after_signing_is_refused()
    {
        var signature = Sign(Body());

        var notification = await Provider().VerifyAndParseAsync(
            Body(orderNumber: "KLK-OTHER000"), Headers(signature), CancellationToken.None);

        notification.ShouldBeNull();
    }

    [Fact]
    public async Task A_body_signed_with_another_key_is_refused()
    {
        var body = Body();

        var notification = await Provider().VerifyAndParseAsync(
            body, Headers(Sign(body, Encoding.UTF8.GetBytes("someone-elses-key"))), CancellationToken.None);

        notification.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    public async Task A_missing_or_unreadable_signature_is_refused(string? signature)
    {
        var secret = new FixedSecret(Key);

        var notification = await Provider(secret).VerifyAndParseAsync(Body(), Headers(signature), CancellationToken.None);

        notification.ShouldBeNull();
    }

    [Fact]
    public async Task A_request_with_no_signature_never_reads_the_key()
    {
        var secret = new FixedSecret(Key);

        await Provider(secret).VerifyAndParseAsync(Body(), Headers(null), CancellationToken.None);

        secret.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"orderNumber\":\"KLK-1\",\"status\":\"delivered\"}")]
    [InlineData("{\"eventId\":\"e\",\"orderNumber\":\"KLK-1\"}")]
    [InlineData("{\"eventId\":1,\"orderNumber\":\"KLK-1\",\"status\":\"delivered\"}")]
    public async Task A_signed_body_that_is_not_the_agreed_shape_is_refused(string body)
    {
        var notification = await Provider().VerifyAndParseAsync(body, Headers(Sign(body)), CancellationToken.None);

        notification.ShouldBeNull();
    }

    [Fact]
    public async Task An_oversized_field_is_refused()
    {
        var body = Body(orderNumber: new string('K', 500));

        var notification = await Provider().VerifyAndParseAsync(body, Headers(Sign(body)), CancellationToken.None);

        notification.ShouldBeNull();
    }

    [Fact]
    public async Task A_key_that_cannot_be_read_is_reported_not_treated_as_a_bad_signature()
    {
        var body = Body();

        await Should.ThrowAsync<DeliveryVerificationUnavailableException>(
            () => Provider(new FixedSecret(null)).VerifyAndParseAsync(body, Headers(Sign(body)), CancellationToken.None));
    }

    [Fact]
    public void The_provider_option_defaults_to_none_and_needs_nothing_else()
    {
        var problems = new List<string>();

        new DeliveryOptions().Validate(problems);

        problems.ShouldBeEmpty();
        new DeliveryOptions().IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public void A_configured_provider_needs_its_secret_as_an_arn_never_inline()
    {
        var missing = new List<string>();
        new DeliveryOptions { Provider = "signed-json" }.Validate(missing);
        missing.ShouldNotBeEmpty();

        var inline = new List<string>();
        new DeliveryOptions { Provider = "signed-json", WebhookSecretArn = "hunter2" }.Validate(inline);
        inline.ShouldContain(p => p.Contains("Secrets Manager ARN", StringComparison.Ordinal));

        var good = new List<string>();
        new DeliveryOptions
        {
            Provider = "signed-json",
            WebhookSecretArn = "arn:aws:secretsmanager:ap-southeast-3:123456789012:secret:koleksi-delivery-AbCdEf",
        }.Validate(good);
        good.ShouldBeEmpty();
    }
}
