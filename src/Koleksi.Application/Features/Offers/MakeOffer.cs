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
        if (await offers.FindActiveAsync(listing.Id, buyerId, ct).ConfigureAwait(false) is not null)
        {
            throw DomainException.For(
                "offer.already_active", "You already have an open offer on this listing.");
        }

        var amount = Money.TryFromRupiah(command.AmountRupiah)
            ?? throw DomainException.For("offer.invalid_amount", "Offer amount must be a positive number.");

        // Throws DomainException for every rule violation — self-offer, at-or-above
        // asking, below the platform floor — propagated unchanged to the caller.
        var offer = Offer.Create(
            listing.Id, buyerId, listing.SellerId, amount, listing.Price,
            command.Message, listing.AutoDeclineBelowPercent, clock);

        await offers.AddAsync(offer, ct).ConfigureAwait(false);

        return new MakeOfferResult(Found: true, offer.Id, offer.Status, offer.ExpiresAt);
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
