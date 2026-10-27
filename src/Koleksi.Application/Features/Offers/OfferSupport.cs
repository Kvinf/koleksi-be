using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Offers;

/// <summary>
/// The push that tells the other party's open pages an offer has moved.
/// </summary>
/// <remarks>
/// A signal and nothing more, exactly like <c>ChatEvents</c>: the offer id, to the other
/// party's private topic. The page that receives it re-reads the offer through its ordinary,
/// authenticated loader, so no amount, name or message ever rides the socket. Best effort:
/// the transition is already saved, and a missed push is repaired by the client's next
/// reconnect or page load.
/// </remarks>
public sealed partial class OfferNotifier(IRealtimeBroadcaster realtime, ILogger<OfferNotifier> logger)
{
    public const string OfferEvent = "offer";

    /// <param name="actorId">Who just acted; the signal goes to the other party.</param>
    public async Task NotifyOtherPartyAsync(Offer offer, string actorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var recipientId = offer.PartyOf(actorId) == OfferParty.Buyer ? offer.SellerId : offer.BuyerId;

        try
        {
            await realtime
                .PublishAsync(RealtimeTopics.User(recipientId), OfferEvent, new { offerId = offer.Id }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNotifyFailed(logger, offer.Id, failure);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Offer {OfferId} was saved, but pushing the change to the other party failed.")]
    private static partial void LogNotifyFailed(ILogger logger, string offerId, Exception exception);
}

/// <summary>Applies an offer's deadline the moment anyone looks at it, since no job does.</summary>
internal static class OfferExpiry
{
    /// <summary>
    /// Persists <see cref="OfferStatus.Expired"/> when the offer has lapsed, so a stale row
    /// stops blocking the buyer's next offer and stops showing as live.
    /// </summary>
    /// <returns>
    /// The offer as it stands afterwards. If another request changed it while this one was
    /// saving, that request's version is returned instead: it is the newer truth.
    /// </returns>
    public static async Task<Offer> ApplyAsync(IOfferRepository offers, Offer offer, IClock clock, CancellationToken ct)
    {
        var before = offer.Version;
        if (!offer.Expire(clock))
        {
            return offer;
        }

        try
        {
            await offers.UpdateAsync(offer, before, ct).ConfigureAwait(false);
            return offer;
        }
        catch (ConcurrencyConflictException)
        {
            return await offers.FindAsync(offer.Id, ct).ConfigureAwait(false) ?? offer;
        }
    }
}
