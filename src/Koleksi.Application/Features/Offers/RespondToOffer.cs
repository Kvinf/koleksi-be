using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Application.Features.Offers;

/// <summary>
/// The shape every offer transition shares: find, apply the deadline, let the domain decide
/// whether this caller may do this now, save conditionally, tell the other party.
/// </summary>
/// <remarks>
/// Who may act in which state is the domain's rule (see <see cref="Offer"/>), not this
/// file's: the handlers only name the transition. The save is conditional on the version
/// that was read, so two simultaneous answers cannot both win.
/// </remarks>
internal static class OfferTransition
{
    public static async Task<OfferOutcome> RunAsync(
        IOfferRepository offers,
        OfferNotifier notifier,
        IClock clock,
        string actingUserId,
        string offerId,
        Action<Offer> transition,
        CancellationToken ct,
        Func<Offer, CancellationToken, Task>? beforeSave = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerId);

        var offer = await offers.FindAsync(offerId, ct).ConfigureAwait(false);
        // A stranger gets the answer a missing offer gets (404), so these routes cannot be used to
        // find out which offer ids exist (QA S-08). Checked before the deadline is applied, so a
        // stranger's request never causes a write either.
        if (offer is null
            || (!string.Equals(offer.BuyerId, actingUserId, StringComparison.Ordinal)
                && !string.Equals(offer.SellerId, actingUserId, StringComparison.Ordinal)))
        {
            return OfferOutcome.NotFound;
        }

        offer = await OfferExpiry.ApplyAsync(offers, offer, clock, ct).ConfigureAwait(false);

        var expectedVersion = offer.Version;
        transition(offer);

        // After the domain has agreed the caller may do this (so its own refusals come first), and
        // before anything is written.
        if (beforeSave is not null)
        {
            await beforeSave(offer, ct).ConfigureAwait(false);
        }

        await offers.UpdateAsync(offer, expectedVersion, ct).ConfigureAwait(false);
        await notifier.NotifyOtherPartyAsync(offer, actingUserId, ct).ConfigureAwait(false);

        return OfferOutcome.Success(offer);
    }

    /// <summary>
    /// Accepting or countering commits the seller to a deal on a listing, so the listing must still
    /// be for sale. Otherwise the deal is dead on arrival: the buyer's checkout refuses an
    /// unavailable listing. Declining and withdrawing are not gated, they only close a negotiation.
    /// </summary>
    public static async Task EnsureListingForSaleAsync(IListingRepository listings, Offer offer, CancellationToken ct)
    {
        var listing = await listings.FindAsync(offer.ListingId, ct).ConfigureAwait(false);
        if (listing is null || listing.Status != Koleksi.Domain.Catalog.ListingStatus.Active)
        {
            throw DomainException.For("offer.listing_unavailable", "This listing is no longer for sale.");
        }
    }
}

/// <summary>
/// Accepts the price on the table: the seller on a pending offer, the buyer on a counter.
/// </summary>
/// <remarks>
/// Accepting is a price commitment, not a purchase — see <see cref="Offer.Accept"/>. The
/// buyer still has to check out separately, at <see cref="Offer.CurrentAmount"/>.
/// </remarks>
public sealed class AcceptOfferHandler(
    IOfferRepository offers, IListingRepository listings, OfferNotifier notifier, ICurrentUser currentUser, IClock clock)
{
    public Task<OfferOutcome> HandleAsync(string offerId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        return OfferTransition.RunAsync(
            offers, notifier, clock, userId, offerId, offer => offer.Accept(userId, clock), ct,
            (offer, token) => OfferTransition.EnsureListingForSaleAsync(listings, offer, token));
    }
}

/// <summary>Declines the price on the table: the seller on a pending offer, the buyer on a counter.</summary>
public sealed class DeclineOfferHandler(
    IOfferRepository offers, OfferNotifier notifier, ICurrentUser currentUser, IClock clock)
{
    public Task<OfferOutcome> HandleAsync(string offerId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        return OfferTransition.RunAsync(offers, notifier, clock, userId, offerId, offer => offer.Decline(userId, clock), ct);
    }
}

/// <summary>Proposes a different price, on behalf of the listing's seller.</summary>
public sealed class CounterOfferHandler(
    IOfferRepository offers, IListingRepository listings, OfferNotifier notifier, ICurrentUser currentUser, IClock clock)
{
    public Task<OfferOutcome> HandleAsync(string offerId, long counterAmountRupiah, CancellationToken ct)
    {
        var sellerId = currentUser.RequireUserId();

        // Checked at the boundary, before the offer is even read: a malformed amount is the
        // caller's mistake whatever state the offer is in.
        var counterAmount = Money.TryFromRupiah(counterAmountRupiah)
            ?? throw DomainException.For("offer.invalid_amount", "Counter amount must be a positive number.");

        return OfferTransition.RunAsync(
            offers, notifier, clock, sellerId, offerId, offer => offer.Counter(counterAmount, sellerId, clock), ct,
            (offer, token) => OfferTransition.EnsureListingForSaleAsync(listings, offer, token));
    }
}

/// <summary>Retracts an offer, on behalf of the buyer who made it.</summary>
public sealed class WithdrawOfferHandler(
    IOfferRepository offers, OfferNotifier notifier, ICurrentUser currentUser, IClock clock)
{
    public Task<OfferOutcome> HandleAsync(string offerId, CancellationToken ct)
    {
        var buyerId = currentUser.RequireUserId();
        return OfferTransition.RunAsync(offers, notifier, clock, buyerId, offerId, offer => offer.Withdraw(buyerId, clock), ct);
    }
}

/// <param name="Found"><c>false</c> when the offer does not exist.</param>
/// <param name="Status">The offer's status after the action.</param>
/// <param name="AmountRupiah">The price on the table: the counter amount once countered.</param>
/// <param name="CounterAmountRupiah">The seller's counter, when there is one.</param>
/// <param name="ExpiresAt">The deadline for whatever the offer now awaits.</param>
public sealed record OfferOutcome(
    bool Found, OfferStatus? Status, long? AmountRupiah, DateTimeOffset? ExpiresAt, long? CounterAmountRupiah = null)
{
    public static OfferOutcome NotFound { get; } = new(false, null, null, null);

    public static OfferOutcome Success(Offer offer) =>
        new(true, offer.Status, offer.CurrentAmount.Rupiah, offer.ExpiresAt, offer.CounterAmount?.Rupiah);
}
