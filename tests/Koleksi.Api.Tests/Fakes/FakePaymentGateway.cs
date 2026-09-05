using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// A programmable <see cref="IPaymentGateway"/>.
/// </summary>
/// <remarks>
/// The production <c>PaymentGateway</c> fetches its webhook signing key from AWS Secrets
/// Manager, which is exactly the one dependency this suite's whole design (see
/// <c>KoleksiApiFactory</c>'s remarks) avoids reaching over the network for. This fake lets
/// <c>PaymentEndpoints</c>/<c>ConfirmOrderPaymentHandler</c>'s own logic — routing, the
/// idempotency claim, the order lookup, marking the listing sold — be proven without ever
/// touching AWS, the same way the other fakes let the rest of the API run without DynamoDB
/// or Cognito.
/// </remarks>
public sealed class FakePaymentGateway : FakeAdapter, IPaymentGateway
{
    /// <summary>What the next <see cref="CreateSessionAsync"/> call returns.</summary>
    public PaymentSession? NextSession { get; set; }

    /// <summary>Thrown by <see cref="CreateSessionAsync"/> when set, mirroring the real stub.</summary>
    public Exception? CreateSessionFailsWith { get; set; }

    /// <summary>What the next <see cref="VerifyAndParseWebhook"/> call returns.</summary>
    public PaymentNotification? NextNotification { get; set; }

    public Task<PaymentSession> CreateSessionAsync(PaymentRequest request, CancellationToken ct)
    {
        ThrowIfFailing();

        if (CreateSessionFailsWith is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(NextSession
            ?? new PaymentSession(request.ReferenceId, new Uri("https://payments.koleksi.test/pay/test"), null, request.ExpiresAt));
    }

    public PaymentNotification? VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        ThrowIfFailing();
        return NextNotification;
    }
}
