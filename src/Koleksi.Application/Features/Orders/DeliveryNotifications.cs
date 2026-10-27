using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Orders;

/// <summary>
/// A courier-tracking provider told us about a parcel: verifies who is talking, then moves the order the same
/// way the sweep's own poll would (<c>Shipped → InTransit → Delivered</c>, which starts the two-day release clock).
/// </summary>
/// <remarks>
/// <para>
/// Reachable only from <c>POST /api/shipping/webhook/{provider}</c>. The only thing that authorises it is the
/// provider's <see cref="IDeliveryWebhookProvider"/> accepting the raw body's signature; there is no signed-in user.
/// A forged "delivered" starts the clock that pays the seller, so an unverified body changes nothing.
/// </para>
/// <para>
/// Safe to repeat and to reorder: an event id is claimed once, a notification for an order already past that
/// point is a no-op, and a stale "in transit" arriving after "delivered" is ignored. The release clock starts at
/// <i>our</i> time of learning, never the provider's, so a late or wrong timestamp cannot shorten the buyer's two days.
/// </para>
/// </remarks>
public sealed partial class ApplyDeliveryNotificationHandler(
    IEnumerable<IDeliveryWebhookProvider> providers,
    IOrderRepository orders,
    IIdempotencyStore idempotency,
    IClock clock,
    ILogger<ApplyDeliveryNotificationHandler> logger)
{
    private static readonly TimeSpan ClaimRetention = TimeSpan.FromDays(1);
    private const int MaxAttempts = 4;

    public async Task<DeliveryWebhookResult> HandleAsync(
        string providerName, string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(headers);

        var provider = providers.FirstOrDefault(p => string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return DeliveryWebhookResult.UnknownProvider;
        }

        var notification = await provider.VerifyAndParseAsync(rawBody, headers, ct).ConfigureAwait(false);
        if (notification is null)
        {
            return DeliveryWebhookResult.Unverified;
        }

        return await idempotency.RunOnceAsync(
            $"delivery.webhook.{provider.Name}",
            notification.EventId,
            ClaimRetention,
            () => ApplyAsync(notification, ct),
            ct).ConfigureAwait(false);
    }

    private async Task<DeliveryWebhookResult> ApplyAsync(DeliveryNotification notification, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var order = await orders.FindByOrderNumberAsync(notification.OrderNumber, ct).ConfigureAwait(false);
            if (order is null)
            {
                // Nothing to do and nothing a retry would change: acknowledge, so the provider stops.
                LogUnknownOrder(logger, notification.OrderNumber);
                return DeliveryWebhookResult.Accepted;
            }

            if (order.EscrowState is EscrowState.Paid)
            {
                // The courier is ahead of the seller marking it shipped. Not final: tell the provider to try again.
                return DeliveryWebhookResult.NotShippedYet;
            }

            var expectedVersion = order.Version;
            if (!DeliveryProgress.Apply(order, notification.State, clock))
            {
                return DeliveryWebhookResult.Accepted;
            }

            try
            {
                await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);
                LogApplied(logger, order.OrderNumber, notification.State);
                return DeliveryWebhookResult.Accepted;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                // The buyer confirmed or disputed at the same moment. Read it again and see what is left to do.
            }
        }

        throw new ConcurrencyConflictException($"Order '{notification.OrderNumber}' kept changing while a delivery notice was applied.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A delivery notice named order {OrderNumber}, which does not exist.")]
    private static partial void LogUnknownOrder(ILogger logger, string orderNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivery notice applied to order {OrderNumber}: {State}.")]
    private static partial void LogApplied(ILogger logger, string orderNumber, DeliveryState state);
}

/// <summary>How a delivery state moves an order. Shared by the sweep's poll and the provider's push.</summary>
public static class DeliveryProgress
{
    /// <summary>
    /// Applies <paramref name="state"/> to <paramref name="order"/> in memory.
    /// </summary>
    /// <returns><c>true</c> when the order changed and must be saved, <c>false</c> when there was nothing to do.</returns>
    public static bool Apply(Order order, DeliveryState state, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (order.EscrowState is not (EscrowState.Shipped or EscrowState.InTransit))
        {
            // Already delivered, released, disputed, cancelled: a late or repeated notice changes nothing.
            return false;
        }

        switch (state)
        {
            case DeliveryState.Delivered:
                order.MarkDelivered(clock);
                return true;
            case DeliveryState.InTransit when order.EscrowState == EscrowState.Shipped:
                order.MarkInTransit(clock);
                return true;
            default:
                return false;
        }
    }
}

/// <param name="Outcome">What happened, for the endpoint to answer with.</param>
public sealed record DeliveryWebhookResult(DeliveryWebhookOutcome Outcome)
{
    public static DeliveryWebhookResult Accepted { get; } = new(DeliveryWebhookOutcome.Accepted);

    public static DeliveryWebhookResult Unverified { get; } = new(DeliveryWebhookOutcome.Unverified);

    public static DeliveryWebhookResult UnknownProvider { get; } = new(DeliveryWebhookOutcome.UnknownProvider);

    public static DeliveryWebhookResult NotShippedYet { get; } = new(DeliveryWebhookOutcome.NotShippedYet);
}

public enum DeliveryWebhookOutcome
{
    /// <summary>Verified and applied, or verified and nothing to apply. Never retried.</summary>
    Accepted = 0,

    /// <summary>The signature did not check out, or the body was not understood.</summary>
    Unverified = 1,

    /// <summary>No provider by that name is configured.</summary>
    UnknownProvider = 2,

    /// <summary>Genuine, but the seller has not marked the order shipped yet; the provider should send it again.</summary>
    NotShippedYet = 3,
}
