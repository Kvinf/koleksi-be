using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Application.Features.Offers;

/// <summary>Accepts an offer, on behalf of the listing's seller.</summary>
/// <remarks>
/// Accepting is a price commitment, not a purchase — see <see cref="Offer.Accept"/>'s own
/// remarks. The buyer still has to check out separately, at <see cref="Offer.Amount"/>.
/// </remarks>
public sealed class AcceptOfferHandler(IOfferRepository offers, ICurrentUser currentUser, IClock clock)
{
    public async Task<OfferOutcome> HandleAsync(string offerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerId);

        var sellerId = currentUser.RequireUserId();
        var offer = await offers.FindAsync(offerId, ct).ConfigureAwait(false);
        if (offer is null)
        {
            return OfferOutcome.NotFound;
        }

        var expectedVersion = offer.Version;
        offer.Accept(sellerId, clock);
        await offers.UpdateAsync(offer, expectedVersion, ct).ConfigureAwait(false);

        return OfferOutcome.Success(offer);
    }
}

/// <summary>Declines an offer, on behalf of the listing's seller.</summary>
public sealed class DeclineOfferHandler(IOfferRepository offers, ICurrentUser currentUser, IClock clock)
{
    public async Task<OfferOutcome> HandleAsync(string offerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerId);

        var sellerId = currentUser.RequireUserId();
        var offer = await offers.FindAsync(offerId, ct).ConfigureAwait(false);
        if (offer is null)
        {
            return OfferOutcome.NotFound;
        }

        var expectedVersion = offer.Version;
        offer.Decline(sellerId, clock);
        await offers.UpdateAsync(offer, expectedVersion, ct).ConfigureAwait(false);

        return OfferOutcome.Success(offer);
    }
}

/// <summary>Proposes a different price, on behalf of the listing's seller.</summary>
public sealed class CounterOfferHandler(IOfferRepository offers, ICurrentUser currentUser, IClock clock)
{
    public async Task<OfferOutcome> HandleAsync(string offerId, long counterAmountRupiah, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerId);

        var sellerId = currentUser.RequireUserId();
        var offer = await offers.FindAsync(offerId, ct).ConfigureAwait(false);
        if (offer is null)
        {
            return OfferOutcome.NotFound;
        }

        var counterAmount = Money.TryFromRupiah(counterAmountRupiah)
            ?? throw DomainException.For("offer.invalid_amount", "Counter amount must be a positive number.");

        var expectedVersion = offer.Version;
        offer.Counter(counterAmount, sellerId, clock);
        await offers.UpdateAsync(offer, expectedVersion, ct).ConfigureAwait(false);

        return OfferOutcome.Success(offer);
    }
}

/// <summary>Retracts an offer, on behalf of the buyer who made it.</summary>
public sealed class WithdrawOfferHandler(IOfferRepository offers, ICurrentUser currentUser, IClock clock)
{
    public async Task<OfferOutcome> HandleAsync(string offerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerId);

        var buyerId = currentUser.RequireUserId();
        var offer = await offers.FindAsync(offerId, ct).ConfigureAwait(false);
        if (offer is null)
        {
            return OfferOutcome.NotFound;
        }

        var expectedVersion = offer.Version;
        offer.Withdraw(buyerId, clock);
        await offers.UpdateAsync(offer, expectedVersion, ct).ConfigureAwait(false);

        return OfferOutcome.Success(offer);
    }
}

/// <param name="Found"><c>false</c> when the offer does not exist.</param>
/// <param name="Status">The offer's status after the action.</param>
/// <param name="AmountRupiah">
/// The current amount on the table — the counter amount, once countered.
/// </param>
public sealed record OfferOutcome(bool Found, OfferStatus? Status, long? AmountRupiah, DateTimeOffset? ExpiresAt)
{
    public static OfferOutcome NotFound { get; } = new(false, null, null, null);

    public static OfferOutcome Success(Offer offer) =>
        new(true, offer.Status, offer.Amount.Rupiah, offer.ExpiresAt);
}
