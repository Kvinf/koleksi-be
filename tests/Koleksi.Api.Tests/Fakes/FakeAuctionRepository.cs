using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IAuctionRepository"/> that honours the version condition.
/// </summary>
/// <remarks>
/// Bidding is the only genuinely contended write in the product, so this fake keeps the
/// two properties the retry logic depends on: every read returns a fresh aggregate, and
/// every save is refused unless the stored version still matches the one the caller read.
/// Without both, <c>PlaceBidHandler</c>'s retry loop would appear to work no matter what
/// it did.
/// </remarks>
public sealed class FakeAuctionRepository : FakeAdapter, IAuctionRepository
{
    private readonly Dictionary<string, Auction> _auctions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Bid>> _bids = new(StringComparer.Ordinal);

    /// <summary>
    /// How many of the next saves to reject with a concurrency conflict, so a test can
    /// make the handler lose a race it is meant to retry.
    /// </summary>
    public int ConflictsToInject { get; set; }

    /// <summary>Seeds storage directly, bypassing the write path.</summary>
    public void Seed(Auction auction)
    {
        ArgumentNullException.ThrowIfNull(auction);
        _auctions[auction.ListingId] = auction;
        _bids.TryAdd(auction.ListingId, []);
    }

    /// <summary>Every bid recorded so far for a listing, oldest first.</summary>
    public IReadOnlyList<Bid> RecordedBids(string listingId) =>
        _bids.TryGetValue(listingId, out var bids) ? bids : [];

    public Task<Auction?> FindAsync(string listingId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(_auctions.TryGetValue(listingId, out var auction) ? Copy(auction) : null);
    }

    public Task AddAsync(Auction auction, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(auction);

        if (!_auctions.TryAdd(auction.ListingId, Copy(auction)))
        {
            throw new ConcurrencyConflictException($"Auction {auction.ListingId} already exists.");
        }

        _bids.TryAdd(auction.ListingId, []);
        return Task.CompletedTask;
    }

    public Task SaveBidAsync(
        Auction auction, IReadOnlyList<Bid> newBids, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(auction);
        ArgumentNullException.ThrowIfNull(newBids);

        if (ConflictsToInject > 0)
        {
            ConflictsToInject--;
            throw new ConcurrencyConflictException("Another bid landed first.");
        }

        if (!_auctions.TryGetValue(auction.ListingId, out var stored) || stored.Version != expectedVersion)
        {
            throw new ConcurrencyConflictException("Another bid landed first.");
        }

        // One transaction in production: the auction and its history rows land together,
        // or neither does. Mirrored here so a test cannot pass against a fake that would
        // have left an auction whose price no bid accounts for.
        _auctions[auction.ListingId] = Copy(auction);
        _bids[auction.ListingId].AddRange(newBids);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Bid>> GetBidHistoryAsync(string listingId, int limit, CancellationToken ct)
    {
        ThrowIfFailing();

        IReadOnlyList<Bid> history = RecordedBids(listingId)
            .OrderByDescending(bid => bid.PlacedAt)
            .ThenByDescending(bid => bid.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        return Task.FromResult(history);
    }

    public Task<IReadOnlyList<Auction>> ListDueForCloseAsync(
        DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ThrowIfFailing();

        IReadOnlyList<Auction> due = _auctions.Values
            .Where(auction => auction.Status == AuctionStatus.Live && auction.EndsAt <= asOf)
            .OrderBy(auction => auction.EndsAt)
            .Take(limit)
            .Select(Copy)
            .ToList();

        return Task.FromResult(due);
    }

    private static Auction Copy(Auction auction) => Auction.Rehydrate(
        auction.ListingId,
        auction.SellerId,
        auction.StartingBid,
        auction.CurrentPrice,
        auction.BidCount,
        auction.StartsAt,
        auction.EndsAt,
        auction.Status,
        auction.LeadingBidderId,
        auction.LeadingBidderUsername,
        auction.Version,
        auction.ReservePrice,
        auction.BuyoutPrice);
}
