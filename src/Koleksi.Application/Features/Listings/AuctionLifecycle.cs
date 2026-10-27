using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Listings;

/// <summary>
/// The two places an auction listing's terms cross a boundary: from the seller's form onto the
/// listing (so a draft remembers them), and from the listing into a live
/// <see cref="Auction"/> at publish.
/// </summary>
/// <remarks>
/// One implementation for create, edit and the dashboard's "List" button. They used to each carry
/// their own copy, and the third (publishing a saved draft) had none: it flipped the listing to
/// Active without ever creating the auction, so a draft auction published from the dashboard had
/// nothing to bid on.
/// </remarks>
public static class AuctionLifecycle
{
    /// <summary>
    /// The duration used when the listing has none stored, matching the sell form's default.
    /// Only reachable for drafts saved before terms were stored with them.
    /// </summary>
    public const int DefaultDurationDays = 3;

    /// <summary>
    /// Stores the seller's chosen terms on the listing, or clears them when the listing is not
    /// an auction. Does not decide whether the terms are valid; <see cref="OpenFor"/> does, at
    /// publish, so an unfinished draft can still be saved.
    /// </summary>
    public static void ApplyTerms(
        Listing listing,
        ListingFormat format,
        int? durationDays,
        long? reservePriceRupiah,
        long? buyoutPriceRupiah,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(clock);

        if (format != ListingFormat.Auction)
        {
            listing.SetAuctionTerms(null, null, null, clock);
            return;
        }

        var reserve = reservePriceRupiah is { } reserveRupiah
            ? Money.TryFromRupiah(reserveRupiah)
                ?? throw DomainException.For("auction.invalid_reserve", "Reserve price must be a positive number.")
            : (Money?)null;
        var buyout = buyoutPriceRupiah is { } buyoutRupiah
            ? Money.TryFromRupiah(buyoutRupiah)
                ?? throw DomainException.For("auction.invalid_buyout", "Buyout price must be a positive number.")
            : (Money?)null;

        listing.SetAuctionTerms(durationDays, reserve, buyout, clock);
    }

    /// <summary>
    /// Creates and opens the auction a published listing runs, from the terms stored on it.
    /// The listing's price is the starting bid.
    /// </summary>
    /// <exception cref="DomainException">The terms break an auction rule (duration, reserve, buyout).</exception>
    public static Auction OpenFor(Listing listing, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(clock);

        var auction = Auction.Create(
            listing.Id,
            listing.SellerId,
            listing.Price,
            TimeSpan.FromDays(listing.AuctionDurationDays ?? DefaultDurationDays),
            clock,
            listing.AuctionReservePrice,
            listing.AuctionBuyoutPrice);

        // Opened immediately rather than left Scheduled: this product has no "list now, start
        // bidding later" concept.
        auction.Open(clock);
        return auction;
    }

    /// <summary>
    /// Decides, without writing anything, which auction a listing being published will run: a fresh one
    /// (none yet, or the last round is over), or the live one it already has. Throws for terms that
    /// break an auction rule, so a refused publish leaves nothing behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A lot can be relisted after its last round ended without a sale or was cancelled, and after a
    /// winner who never paid has let <see cref="Auction.WinnerPaymentWindow"/> run out: each gets a
    /// fresh auction over the same listing id (D-05). A winner still inside the window keeps the lot.
    /// </para>
    /// <para>
    /// A listing that has never been live cannot have taken a bid, so any auction found for it is the
    /// leftover of a publish that failed half way. It is replaced, so the terms the seller has now
    /// are the ones that run.
    /// </para>
    /// </remarks>
    /// <param name="neverLive">Whether the listing was unpublished (a draft) before this publish.</param>
    /// <exception cref="DomainException"><c>auction.awaiting_payment</c>: a winner still has time to pay.</exception>
    public static async Task<AuctionPublishPlan> PlanAsync(
        IAuctionRepository auctions, Listing listing, bool neverLive, IClock clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auctions);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(clock);

        if (listing.Format != ListingFormat.Auction)
        {
            return AuctionPublishPlan.None;
        }

        var existing = await auctions.FindAsync(listing.Id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return new AuctionPublishPlan(OpenFor(listing, clock), Replacing: null, IsNew: true);
        }

        switch (existing.Status)
        {
            case AuctionStatus.Live or AuctionStatus.Scheduled when !(neverLive && existing.BidCount == 0):
                return new AuctionPublishPlan(existing, Replacing: null, IsNew: false);

            case AuctionStatus.Ended when !existing.IsWinnerPaymentLapsed(clock):
                throw DomainException.For(
                    "auction.awaiting_payment",
                    "This auction ended with a winner who still has time to pay.");

            default:
                return new AuctionPublishPlan(OpenFor(listing, clock), existing, IsNew: true);
        }
    }

    /// <summary>
    /// Writes what <see cref="PlanAsync"/> decided. Called BEFORE the listing is saved as Active, never
    /// after: an auction on a listing that is not yet Active takes no bids (the bid and buyout handlers
    /// check), so a failure between the two writes leaves nothing biddable, and publishing again picks
    /// the auction up. The other order left an Active listing with nothing to bid on (D-09).
    /// </summary>
    public static async Task CommitAsync(IAuctionRepository auctions, AuctionPublishPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auctions);
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.IsNew || plan.Auction is null)
        {
            return;
        }

        if (plan.Replacing is null)
        {
            await auctions.AddAsync(plan.Auction, ct).ConfigureAwait(false);
        }
        else
        {
            // Overwrites the earlier round in place, conditioned on its version. Its bid rows stay stored
            // as history; the item page shows only bids placed since this round started.
            await auctions.SaveBidAsync(plan.Auction, [], plan.Replacing.Version, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// What publishing an auction listing will do to its <see cref="Auction"/>, decided entirely in
/// memory before anything is written. See <see cref="AuctionLifecycle.PlanAsync"/>.
/// </summary>
/// <param name="Auction">The auction the published listing runs, or <c>null</c> for a listing that is not an auction.</param>
/// <param name="Replacing">The earlier auction this one overwrites, when there is one.</param>
/// <param name="IsNew"><c>false</c> when an existing live auction is simply kept.</param>
public sealed record AuctionPublishPlan(Auction? Auction, Auction? Replacing, bool IsNew)
{
    public static AuctionPublishPlan None { get; } = new(null, null, false);
}
