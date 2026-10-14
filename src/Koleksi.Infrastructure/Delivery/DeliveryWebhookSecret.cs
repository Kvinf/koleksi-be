using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Koleksi.Application.Abstractions;

namespace Koleksi.Infrastructure.Delivery;

/// <summary>
/// The delivery webhook's signing key, read once from Secrets Manager with the Lambda's role and kept in memory.
/// </summary>
/// <remarks>
/// A gate rather than <c>Lazy&lt;Task&gt;</c>, for the reason <c>PaymentSecret</c> gives: a failed first read must
/// not be cached for the life of the instance. A failure becomes <see cref="DeliveryVerificationUnavailableException"/>
/// so the endpoint can answer 503 and the provider redelivers, instead of a bare 500 or, worse, a refusal.
/// </remarks>
internal sealed class DeliveryWebhookSecret(IAmazonSecretsManager secrets, DeliveryOptions options) : IDeliveryWebhookSecret, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? cached;

    public void Dispose() => gate.Dispose();

    public async Task<byte[]> GetAsync(CancellationToken ct)
    {
        if (cached is { } ready)
        {
            return ready;
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (cached is { } raced)
            {
                return raced;
            }

            try
            {
                var response = await secrets.GetSecretValueAsync(
                    new GetSecretValueRequest { SecretId = options.WebhookSecretArn }, ct).ConfigureAwait(false);

                if (string.IsNullOrEmpty(response.SecretString))
                {
                    throw new DeliveryVerificationUnavailableException("The delivery webhook secret is empty.");
                }

                cached = System.Text.Encoding.UTF8.GetBytes(response.SecretString);
                return cached;
            }
            catch (Exception failure) when (failure is AmazonSecretsManagerException or HttpRequestException or TimeoutException)
            {
                throw new DeliveryVerificationUnavailableException("The delivery webhook secret could not be read.", failure);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>The stand-in for a stage with no provider: its tracker never claims to track, and it has no webhooks.</summary>
internal sealed class PushOnlyDeliveryTracker : IDeliveryTracker
{
    /// <summary>A provider that pushes is configured: the order page can promise the two-day release.</summary>
    public bool IsConfigured => true;

    /// <summary>Nothing to ask; the provider tells us.</summary>
    public bool CanPoll => false;

    public Task<DeliveryReport> CheckAsync(Koleksi.Domain.Orders.Order order, CancellationToken ct) =>
        Task.FromResult(DeliveryReport.Unknown);
}
