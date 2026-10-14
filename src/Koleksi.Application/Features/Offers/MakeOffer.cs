using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Application.Features.Offers;

/// <summary>
/// Makes an offer on a <c>nego</c> listing on behalf of the authenticated caller.
/// </summary>
/// <remarks>
/// The buyer comes from <see cref="ICurrentUser"/>, never from the request, the same
/// reasoning every other write in this product follows: a body that named its own buyer
/// would let anyone offer as anyone.
/// </remarks>
public sealed class MakeOfferHandler(
    IListingRepository listings,
    IOfferRepository offers,
    OfferNotifier notifier,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<MakeOfferResult> HandleAsync(MakeOfferCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var buyerId = currentUser.RequireUserId();

        var listing = await listings.FindAsync(command.ListingId, ct).ConfigureAwait(false);
        if (listing is null || listing.Status != ListingStatus.Active || listing.Format != ListingFormat.AcceptOffers)
        {
            // Same coarse answer whether the listing is gone, unpublished, or simply not a
            // nego listing — none of those distinctions change what the caller should do.
            return MakeOfferResult.NotFound;
        }

        // One open offer per buyer per listing. Checked here rather than left to a unique
        // constraint alone, so the caller gets a named reason instead of a write conflict.
        // The deadline is applied first: an offer that lapsed while nobody looked is not "open",
        // and must not keep the buyer from trying again.
        if (await offers.FindActiveAsync(listing.Id, buyerId, ct).ConfigureAwait(false) is { } existing)
        {
            existing = await OfferExpiry.ApplyAsync(offers, existing, clock, ct).ConfigureAwait(false);
            if (existing.Status is OfferStatus.Pending or OfferStatus.Countered or OfferStatus.Accepted
                or OfferStatus.CheckedOut)
            {
                throw DomainException.For(
                    "offer.already_active", "You already have an open offer on this listing.");
            }
        }

        var amount = Money.TryFromRupiah(command.AmountRupiah)
            ?? throw DomainException.For("offer.invalid_amount", "Offer amount must be a positive number.");

        // Throws DomainException for every rule violation — self-offer, at-or-above
        // asking, below the platform floor — propagated unchanged to the caller.
        // No usernames are stored on the offer. The sign-in name (cognito:username) can be an email
        // address, and the stored seller name is the same legacy value (see
        // IListingRepository.FindSellerUsernameAsync); nothing reads either, because every offer view
        // resolves the parties' public handles from their ids at read time. Offers stored before this
        // keep their old values, which stay unread.
        var offer = Offer.Create(
            listing.Id, buyerId, listing.SellerId, amount, listing.Price,
            command.Message, listing.AutoDeclineBelowPercent, clock);

        try
        {
            await offers.AddAsync(offer, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            // Two identical requests raced past the check above and this one lost the write
            // (the one-open-offer-per-buyer pointer already exists, or the other request's
            // transaction was touching it at that instant). The winner's offer is the buyer's
            // open offer, so the loser gets the same named answer as a sequential repeat,
            // not a "server busy" 503 that invites a pointless retry.
            if (await AnotherOfferIsOpenAsync(listing.Id, buyerId, ct).ConfigureAwait(false))
            {
                throw DomainException.For(
                    "offer.already_active", "You already have an open offer on this listing.");
            }

            throw;
        }

        // A pending offer is news for the seller; one the seller's own threshold already
        // declined is not, since they never see it as awaiting them.
        if (offer.Status == OfferStatus.Pending)
        {
            await notifier.NotifyOtherPartyAsync(offer, buyerId, ct).ConfigureAwait(false);
        }

        return new MakeOfferResult(Found: true, offer.Id, offer.Status, offer.ExpiresAt);
    }

    /// <summary>
    /// Re-reads after a lost write to see whether the winner's offer is visible yet.
    /// </summary>
    /// <remarks>
    /// When DynamoDB cancels the loser for a conflicting transaction, the winner may still be
    /// committing, so the offer can take a moment to appear. A few short looks cover that
    /// window; if nothing turns up the conflict was something else and the caller rethrows it.
    /// </remarks>
    private async Task<bool> AnotherOfferIsOpenAsync(string listingId, string buyerId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(25 * attempt, ct).ConfigureAwait(false);
            }

            if (await offers.FindActiveAsync(listingId, buyerId, ct).ConfigureAwait(false) is not null)
            {
                return true;
            }
        }

        return false;
    }
}

/// <param name="ListingId">The nego listing being offered on.</param>
/// <param name="AmountRupiah">The buyer's proposed price.</param>
/// <param name="Message">Optional note, capped and normalised by the domain.</param>
public sealed record MakeOfferCommand(string ListingId, long AmountRupiah, string? Message);

/// <param name="Found"><c>false</c> when the listing does not exist or does not accept offers.</param>
/// <param name="Status">
/// Usually <c>Pending</c> — <c>Declined</c> only when the seller's own auto-decline
/// threshold refused it immediately (see <see cref="Offer.Create"/>).
/// </param>
public sealed record MakeOfferResult(bool Found, string? OfferId, OfferStatus? Status, DateTimeOffset? ExpiresAt)
{
    public static MakeOfferResult NotFound { get; } = new(false, null, null, null);
}
