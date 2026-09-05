using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Koleksi.Infrastructure.Payments;

/// <summary>
/// Holds the webhook signing key, fetched once from Secrets Manager.
/// </summary>
/// <remarks>
/// <para>
/// The key is read by ARN using the Lambda's own role and kept in memory only. It is never
/// an environment variable, never in a template, and never in configuration — all three
/// are readable by anyone with console access to the function, and environment variables
/// in particular are printed by well-meaning diagnostic code.
/// </para>
/// <para>
/// Registered as a singleton so a warm execution environment fetches it once rather than
/// on every webhook, and so a Secrets Manager outage cannot stall payment processing that
/// was already working.
/// </para>
/// </remarks>
internal sealed class PaymentSecret(IAmazonSecretsManager secrets, PaymentOptions options) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _cached;

    // A gate rather than Lazy<Task<byte[]>>: Lazy caches a faulted task permanently, so a
    // single Secrets Manager blip on a cold start would leave this instance unable to
    // verify a webhook for the rest of the execution environment's life. Here a failed
    // fetch simply leaves the cache empty and the next webhook tries again.
    public void Dispose() => _gate.Dispose();

    public async Task<byte[]> GetAsync(CancellationToken ct)
    {
        if (_cached is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate: several concurrent webhooks on a cold start would
            // otherwise each make their own Secrets Manager call.
            if (_cached is { } raced)
            {
                return raced;
            }

            var response = await secrets.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = options.SecretArn },
                ct).ConfigureAwait(false);

            _cached = System.Text.Encoding.UTF8.GetBytes(
                response.SecretString
                ?? throw new InvalidOperationException(
                    "The payment webhook secret is empty. Signature verification cannot proceed."));

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Talks to the payment processor and interprets its callbacks.
/// </summary>
/// <remarks>
/// <para>
/// The design specifies a gateway hand-off with no in-app method picker: the processor
/// owns QRIS, the e-wallets, and the virtual accounts. We never see a card number, a
/// wallet credential, or a bank token, which keeps the whole cardholder-data problem
/// outside our boundary.
/// </para>
/// <para>
/// <b>Checkout is scaffolded, verification is not.</b> Creating a payment session needs
/// the processor's live API contract and credentials, which are not part of this pass —
/// see <c>CONTINUATION.md</c>. Webhook verification is implemented in full regardless,
/// because it is the half that decides whether money moved, and shipping it half-done
/// would be worse than not shipping it.
/// </para>
/// </remarks>
internal sealed partial class PaymentGateway(
    PaymentSecret secret,
    ILogger<PaymentGateway> logger) : IPaymentGateway
{
    /// <summary>Header the processor puts its HMAC in.</summary>
    private const string SignatureHeader = "Signature";

    public Task<PaymentSession> CreateSessionAsync(PaymentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // TODO(checkout): inject PaymentOptions, POST to {BaseUrl}/checkout/v1/payment with
        // the merchant client id and a signed request header, then map the response into a
        // PaymentSession. Blocked on the
        // processor's sandbox credentials. Deliberately throws rather than returning a
        // fabricated session: a checkout that silently produces a dead redirect is worse
        // than one that fails at the point of the missing integration.
        throw DomainException.For(
            "payment.not_configured",
            "Online payment is not available yet. Please try again later.");
    }

    public PaymentNotification? VerifyAndParseWebhook(
        string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(headers);

        // Blocking on an async fetch is not something to do lightly, but the interface is
        // synchronous by design — it must be callable from a request filter before the
        // body is bound — and the secret is cached after the first webhook, so this waits
        // only on a cold start.
        var key = secret.GetAsync(CancellationToken.None).GetAwaiter().GetResult();

        var signature = headers.TryGetValue(SignatureHeader, out var value) ? value : null;

        if (!PaymentWebhookVerifier.IsAuthentic(rawBody, signature, key))
        {
            // Logged without the body and without the offered signature. A forged webhook
            // is attacker-controlled text, and writing it to CloudWatch puts it in front of
            // whoever reads the logs.
            LogSignatureRejected(logger);
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.OrderId))
            {
                LogPayloadUnreadable(logger);
                return null;
            }

            var amount = Money.TryFromRupiah(payload.Amount);
            if (amount is null)
            {
                LogPayloadUnreadable(logger);
                return null;
            }

            return new PaymentNotification(
                ReferenceId: payload.OrderId,
                Status: MapStatus(payload.TransactionStatus),
                SettledAmount: amount.Value,
                ProcessorReference: payload.TransactionId ?? string.Empty,

                // Falls back to the processor's transaction id so duplicate deliveries are
                // still detectable when no separate event id is sent.
                EventId: payload.EventId ?? payload.TransactionId ?? payload.OrderId);
        }
        catch (JsonException)
        {
            // The signature verified, so this is our processor sending a shape we do not
            // understand — a contract change, not an attack. Still refused: acting on a
            // payload we cannot read is how an order gets marked paid by accident.
            LogPayloadUnreadable(logger);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static PaymentStatus MapStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "settlement" or "capture" or "success" or "paid" => PaymentStatus.Settled,
        "pending" => PaymentStatus.Pending,
        "expire" or "expired" => PaymentStatus.Expired,

        // Everything else, including anything unrecognised, is treated as not-paid. The
        // default must never be "settled": an unknown status that defaults to paid is an
        // order shipped for free.
        _ => PaymentStatus.Failed,
    };

    /// <summary>The subset of the processor's callback we act on.</summary>
    private sealed record WebhookPayload(
        string? OrderId,
        long Amount,
        string? TransactionStatus,
        string? TransactionId,
        string? EventId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A payment webhook failed signature verification and was discarded.")]
    private static partial void LogSignatureRejected(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "A payment webhook verified but could not be parsed. Check the processor's contract.")]
    private static partial void LogPayloadUnreadable(ILogger logger);
}
