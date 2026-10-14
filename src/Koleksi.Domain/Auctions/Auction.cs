using Koleksi.Domain.Common;

namespace Koleksi.Domain.Auctions;

/// <summary>Lifecycle of an auction.</summary>
public enum AuctionStatus
{
    /// <summary>Created but not yet open for bidding.</summary>
    Scheduled = 0,

    /// <summary>Accepting bids.</summary>
    Live = 1,

    /// <summary>Closed with at least one bid. Awaiting the winner's payment.</summary>
    Ended = 2,

    /// <summary>Closed with no bids, or below reserve. Nothing to settle.</summary>
    EndedWithoutSale = 3,

    /// <summary>Withdrawn before close.</summary>
    Cancelled = 4,
}

/// <summary>
/// The bidding half of an auction listing: current price, leader, close time, and the
/// rules that govern them.
/// </summary>
/// <remarks>
/// <para><b>Highest bid wins, plainly.</b> Whatever amount a bidder submits becomes the
/// visible price, provided it clears <see cref="NextMinimumBid"/>. There is no hidden
/// ceiling and no automatic counter-bidding on anyone's behalf — the number on the page
/// is always exactly what the leading bidder offered.</para>
///
/// <para><b>Anti-sniping.</b> A bid inside the final <see cref="AntiSnipeWindow"/>
/// pushes the close time out, so an auction cannot be won by arriving one second before
/// the end. This is stated on the auction page and on the sell form.</para>
///
/// <para><b>Concurrency.</b> Bidding is the one place where two requests genuinely race.
/// Every mutation bumps <see cref="Version"/>, and the repository writes with a
/// condition on the previous value, so a losing concurrent write is rejected and
/// retried rather than silently overwriting a competing bid.</para>
/// </remarks>
public sealed class Auction
{
    /// <summary>A bid in the last two minutes extends the auction by the same amount.</summary>
    public static readonly TimeSpan AntiSnipeWindow = TimeSpan.FromMinutes(2);

    /// <summary>Shortest auction a seller may schedule.</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromHours(1);

    /// <summary>Longest auction a seller may schedule.</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(14);

    /// <summary>
    /// How long the winner of an <see cref="AuctionStatus.Ended"/> auction has to start paying
    /// before the item goes back to the seller.
    /// </summary>
    /// <remarks>
    /// Without a deadline a winner who never pays locks the item for ever: the seller cannot
    /// archive or relist a lot that still has a winner owed (D-01), and nothing else would
    /// release it. The owner's rule is thirty minutes, to <i>pay</i>, counted from the close: the
    /// order a winner opens inherits this same deadline rather than getting a fresh window (see
    /// <c>Order.AuctionPaymentWindow</c>). A winner who misses it is given a warning
    /// (<c>AccountStanding</c>), and the lot goes back to the seller.
    /// </remarks>
    public static readonly TimeSpan WinnerPaymentWindow = Orders.Order.AuctionPaymentWindow;

    private Auction(
        string listingId,
        string sellerId,
        Money startingBid,
        Money? reservePrice,
        Money? buyoutPrice,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        ListingId = listingId;
        SellerId = sellerId;
        StartingBid = startingBid;
        ReservePrice = reservePrice;
        BuyoutPrice = buyoutPrice;
        CurrentPrice = startingBid;
        StartsAt = startsAt;
        EndsAt = endsAt;
        Status = AuctionStatus.Scheduled;
    }

    /// <summary>Shares the listing's id — the two aggregates describe the same thing.</summary>
    public string ListingId { get; }

    public string SellerId { get; }
    public Money StartingBid { get; }

    /// <summary>
    /// The lowest winning price the seller will accept, shown openly alongside the
    /// starting bid — not a hidden eBay-style reserve. <c>null</c> means any bid wins.
    /// </summary>
    public Money? ReservePrice { get; }

    /// <summary>
    /// A fixed price that ends the auction immediately as a sale, at any point during
    /// bidding — not only before the first bid. <c>null</c> means there is no buyout.
    /// </summary>
    public Money? BuyoutPrice { get; }

    /// <summary>
    /// Whether the current price has reached <see cref="ReservePrice"/>. Always <c>true</c>
    /// when no reserve was set.
    /// </summary>
    public bool ReserveMet => ReservePrice is not { } reserve || CurrentPrice >= reserve;

    /// <summary>The publicly visible current bid.</summary>
    public Money CurrentPrice { get; private set; }

    public int BidCount { get; private set; }
    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset EndsAt { get; private set; }
    public AuctionStatus Status { get; private set; }

    /// <summary>
    /// When the auction actually closed: the sweep's close, or the moment of a buyout (which is
    /// before <see cref="EndsAt"/>). <c>null</c> while it is open, and on rows stored before this
    /// was kept.
    /// </summary>
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>
    /// When the winner must have started paying by, or <c>null</c> unless the auction ended with a
    /// winner. Rows that predate <see cref="ClosedAt"/> count from <see cref="EndsAt"/>.
    /// </summary>
    public DateTimeOffset? PaymentDueAt => Status == AuctionStatus.Ended
        ? (ClosedAt ?? EndsAt) + WinnerPaymentWindow
        : null;

    /// <summary>
    /// Whether the winner's time to start paying has run out, so the item is the seller's again.
    /// Checked lazily wherever it matters (archive, relist, the winner's checkout); no sweep owns it.
    /// </summary>
    public bool IsWinnerPaymentLapsed(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return PaymentDueAt is { } due && clock.UtcNow >= due;
    }

    /// <summary>
    /// When the winner's payment was settled one way or the other: they paid, or their thirty minutes ran
    /// out and the lapse was recorded. <c>null</c> while it is still open. It is what takes the auction off
    /// the winner-payment sweep.
    /// </summary>
    public DateTimeOffset? WinnerPaymentResolvedAt { get; private set; }

    /// <summary>Records that the winner's payment is settled (paid, or lapsed and dealt with).</summary>
    public void ResolveWinnerPayment(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status != AuctionStatus.Ended || WinnerPaymentResolvedAt is not null)
        {
            return;
        }

        WinnerPaymentResolvedAt = clock.UtcNow;
        Version++;
    }

    /// <summary>The bidder currently winning, or <c>null</c> before the first bid.</summary>
    public string? LeadingBidderId { get; private set; }

    public string? LeadingBidderUsername { get; private set; }

    /// <summary>Optimistic-concurrency token. Incremented on every accepted mutation.</summary>
    public long Version { get; private set; }

    /// <summary>Whether the auction is inside the window where a bid extends the close time.</summary>
    public bool IsEndingSoon(IClock clock) => Status == AuctionStatus.Live
        && EndsAt - clock.UtcNow <= TimeSpan.FromMinutes(5);

    /// <summary>
    /// The bid step for a given price, on the tiered ladder Indonesian collectibles
    /// auctions conventionally use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A flat increment behaves badly at both ends of the range: Rp10.000 steps turn a
    /// Rp15.000.000 watch into a thousand-bid war, while Rp500.000 steps price bidders
    /// out of a Rp200.000 card entirely. The ladder keeps the step near 2–5% of the
    /// price across the whole range.
    /// </para>
    /// <para>
    /// The step is a function of the price it is applied to, not of the current price.
    /// When a challenger leaps past a leader's maximum, the settling price is derived
    /// from that maximum, so it must use that maximum's tier — otherwise a bid that
    /// jumps from Rp1.000.000 to Rp5.000.000 would settle using the Rp50.000 step
    /// belonging to the price it left behind.
    /// </para>
    /// </remarks>
    public static Money IncrementFor(Money price) => Money.FromRupiah(price.Rupiah switch
    {
        < 100_000 => 5_000,
        < 500_000 => 10_000,
        < 1_000_000 => 25_000,
        < 5_000_000 => 50_000,
        < 25_000_000 => 100_000,
        _ => 250_000,
    });

    /// <summary>The smallest step above the current price.</summary>
    public Money MinimumIncrement => IncrementFor(CurrentPrice);

    /// <summary>The smallest amount the next bidder may submit.</summary>
    public Money NextMinimumBid => BidCount == 0 ? StartingBid : CurrentPrice + MinimumIncrement;

    /// <summary>Opens an auction for a listing.</summary>
    public static Auction Create(
        string listingId,
        string sellerId,
        Money startingBid,
        TimeSpan duration,
        IClock clock,
        Money? reservePrice = null,
        Money? buyoutPrice = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentNullException.ThrowIfNull(clock);

        if (duration < MinimumDuration || duration > MaximumDuration)
        {
            throw DomainException.For(
                "auction.invalid_duration",
                $"Auction duration must be between {MinimumDuration.TotalHours} hours and {MaximumDuration.TotalDays} days.");
        }

        if (buyoutPrice is { } buyout && buyout <= startingBid)
        {
            throw DomainException.For(
                "auction.invalid_buyout",
                $"Buyout price must be higher than the starting bid ({startingBid}).");
        }

        if (reservePrice is { } reserve
            && (reserve < startingBid || (buyoutPrice is { } cap && reserve > cap)))
        {
            throw DomainException.For(
                "auction.invalid_reserve",
                $"Reserve price must be between the starting bid ({startingBid}) and the buyout price, if any.");
        }

        var now = clock.UtcNow;
        return new Auction(listingId, sellerId, startingBid, reservePrice, buyoutPrice, now, now + duration);
    }

    /// <summary>Reconstructs an auction from storage.</summary>
    public static Auction Rehydrate(
        string listingId,
        string sellerId,
        Money startingBid,
        Money currentPrice,
        int bidCount,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        AuctionStatus status,
        string? leadingBidderId,
        string? leadingBidderUsername,
        long version,
        Money? reservePrice = null,
        Money? buyoutPrice = null,
        DateTimeOffset? closedAt = null,
        DateTimeOffset? winnerPaymentResolvedAt = null) =>
        new(listingId, sellerId, startingBid, reservePrice, buyoutPrice, startsAt, endsAt)
        {
            ClosedAt = closedAt,
            WinnerPaymentResolvedAt = winnerPaymentResolvedAt,
            CurrentPrice = currentPrice,
            BidCount = bidCount,
            Status = status,
            LeadingBidderId = leadingBidderId,
            LeadingBidderUsername = leadingBidderUsername,
            Version = version,
        };

    /// <summary>Moves a scheduled auction to live once its start time has passed.</summary>
    public void Open(IClock clock)
    {
        if (Status != AuctionStatus.Scheduled)
        {
            throw DomainException.For("auction.not_scheduled", $"A {Status} auction cannot be opened.");
        }

        if (clock.UtcNow < StartsAt)
        {
            throw DomainException.For("auction.not_started", "This auction has not started yet.");
        }

        Status = AuctionStatus.Live;
        Version++;
    }

    /// <summary>
    /// Places a bid and returns everything that changed.
    /// </summary>
    /// <param name="bidderId">Cognito subject of the bidder.</param>
    /// <param name="bidderUsername">The bidder's handle, for the masked history.</param>
    /// <param name="amount">The amount to bid. Becomes the new visible price outright.</param>
    /// <param name="clock">Time source.</param>
    /// <exception cref="DomainException">The bid violates an auction rule.</exception>
    public BidOutcome PlaceBid(string bidderId, string bidderUsername, Money amount, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bidderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bidderUsername);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.UtcNow;
        EnsureBiddable(bidderId, now);

        if (string.Equals(LeadingBidderId, bidderId, StringComparison.Ordinal))
        {
            // Already winning — a second bid from the same person would just be them
            // paying more for no reason, and there is no other bidder to actually beat.
            throw DomainException.For(
                "auction.already_leading",
                "You are already the highest bidder.");
        }

        if (amount < NextMinimumBid)
        {
            throw DomainException.For(
                "auction.bid_too_low",
                $"Bid must be at least {NextMinimumBid}.");
        }

        // A bid at or above the buy-it-now price is a buyout in all but name, and as a bid it would
        // set a visible price the later buyout then LOWERS (a Rp1.5M leader beaten by a Rp1.0M
        // buyout costs the seller the difference). The bidder who wants the lot pays the buyout.
        if (BuyoutPrice is { } buyoutCap && amount >= buyoutCap)
        {
            throw DomainException.For(
                "auction.bid_reaches_buyout",
                $"A bid must be below the buy-it-now price ({buyoutCap}). Use buy it now to take the item.");
        }

        var previousLeader = LeadingBidderId;
        var bid = NewBid(bidderId, bidderUsername, amount, now);

        CurrentPrice = amount;
        TakeLead(bidderId, bidderUsername);
        BidCount++;
        var extended = ExtendIfSniped(now);
        Version++;

        return new BidOutcome(
            Bids: [bid],
            IsLeading: true,
            CurrentPrice: CurrentPrice,
            EndsAt: EndsAt,
            WasExtended: extended,
            OutbidUserId: previousLeader is not null && !string.Equals(previousLeader, bidderId, StringComparison.Ordinal)
                ? previousLeader
                : null);
    }

    /// <summary>
    /// Closes the auction once its end time has passed.
    /// </summary>
    /// <remarks>
    /// Driven by a scheduled sweep rather than by page views, so an auction with no
    /// traffic still closes on time and the winner is notified.
    /// </remarks>
    public void Close(IClock clock)
    {
        if (Status is not (AuctionStatus.Live or AuctionStatus.Scheduled))
        {
            throw DomainException.For("auction.not_closable", $"A {Status} auction cannot be closed.");
        }

        if (clock.UtcNow < EndsAt)
        {
            throw DomainException.For("auction.not_ended", "This auction has not reached its end time.");
        }

        Status = LeadingBidderId is null || !ReserveMet ? AuctionStatus.EndedWithoutSale : AuctionStatus.Ended;
        ClosedAt = clock.UtcNow;
        Version++;
    }

    /// <summary>
    /// Ends the auction immediately as a sale at <see cref="BuyoutPrice"/>, skipping the
    /// rest of the bidding.
    /// </summary>
    /// <remarks>
    /// Available for the whole auction, not only before the first bid: a buyer who wants
    /// certainty can always pay the seller's named price rather than keep bidding. Whoever
    /// was leading loses the auction with no notification — this product has none yet, the
    /// same gap <see cref="PlaceBid"/>'s <c>OutbidUserId</c> is meant to eventually close.
    /// </remarks>
    /// <exception cref="DomainException">
    /// No buyout price was set, or the auction is not currently biddable.
    /// </exception>
    public BidOutcome Buyout(string buyerId, string buyerUsername, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerUsername);
        ArgumentNullException.ThrowIfNull(clock);

        if (BuyoutPrice is not { } buyoutPrice)
        {
            throw DomainException.For("auction.no_buyout", "This auction has no buy-it-now price.");
        }

        var now = clock.UtcNow;
        EnsureBiddable(buyerId, now);

        // A buyout never lowers what the bidding already reached. New bids cannot get here (see
        // PlaceBid), but an auction stored before that rule can hold a price at or above its buyout,
        // and selling it at the lower number would hand the difference to the buyer.
        if (CurrentPrice >= buyoutPrice)
        {
            throw DomainException.For(
                "auction.buyout_unavailable",
                "Bidding has already reached the buy-it-now price, so it can no longer be used.");
        }

        var previousLeader = LeadingBidderId;
        var bid = NewBid(buyerId, buyerUsername, buyoutPrice, now);

        CurrentPrice = buyoutPrice;
        TakeLead(buyerId, buyerUsername);
        BidCount++;
        Status = AuctionStatus.Ended;
        ClosedAt = now;
        Version++;

        return new BidOutcome(
            Bids: [bid],
            IsLeading: true,
            CurrentPrice: buyoutPrice,
            EndsAt: EndsAt,
            WasExtended: false,
            OutbidUserId: previousLeader is not null && !string.Equals(previousLeader, buyerId, StringComparison.Ordinal)
                ? previousLeader
                : null);
    }

    /// <summary>Withdraws the auction before it closes.</summary>
    /// <remarks>
    /// Refused once bidding has started: bidders have committed real maxima, and
    /// letting a seller cancel after seeing the price is the classic way to run an
    /// auction as free price discovery.
    /// </remarks>
    public void Cancel(string requestedByUserId)
    {
        if (!string.Equals(SellerId, requestedByUserId, StringComparison.Ordinal))
        {
            throw DomainException.For("auction.forbidden", "Only the seller can cancel this auction.");
        }

        if (BidCount > 0)
        {
            throw DomainException.For(
                "auction.has_bids",
                "An auction with bids cannot be cancelled. Contact support to request a review.");
        }

        Status = AuctionStatus.Cancelled;
        Version++;
    }

    private void EnsureBiddable(string bidderId, DateTimeOffset now)
    {
        if (Status != AuctionStatus.Live)
        {
            throw DomainException.For("auction.not_live", $"This auction is {Status}.");
        }

        if (now >= EndsAt)
        {
            throw DomainException.For("auction.ended", "This auction has ended.");
        }

        // Shill bidding: a seller pushing up their own lot. Blocking the seller's own
        // subject is the floor, not the ceiling — related-account detection belongs in
        // a fraud service, and is noted in the roadmap.
        if (string.Equals(SellerId, bidderId, StringComparison.Ordinal))
        {
            throw DomainException.For("auction.seller_cannot_bid", "You cannot bid on your own auction.");
        }
    }

    private bool ExtendIfSniped(DateTimeOffset now)
    {
        var remaining = EndsAt - now;
        if (remaining > AntiSnipeWindow)
        {
            return false;
        }

        EndsAt = now + AntiSnipeWindow;
        return true;
    }

    private void TakeLead(string bidderId, string username)
    {
        LeadingBidderId = bidderId;
        LeadingBidderUsername = username;
    }

    private Bid NewBid(string bidderId, string username, Money amount, DateTimeOffset at) =>
        new(EntityId.New(at), ListingId, bidderId, username, amount, at);
}

/// <summary>Everything that changed as a result of a bid.</summary>
/// <param name="Bids">History rows to append, oldest first.</param>
/// <param name="IsLeading">Whether the bidder who just acted now holds the lead.</param>
/// <param name="CurrentPrice">The new visible price.</param>
/// <param name="EndsAt">The close time, which anti-sniping may have moved.</param>
/// <param name="WasExtended">Whether anti-sniping extended the auction.</param>
/// <param name="OutbidUserId">The bidder who just lost the lead, if any, so they can be notified.</param>
public sealed record BidOutcome(
    IReadOnlyList<Bid> Bids,
    bool IsLeading,
    Money CurrentPrice,
    DateTimeOffset EndsAt,
    bool WasExtended,
    string? OutbidUserId);
