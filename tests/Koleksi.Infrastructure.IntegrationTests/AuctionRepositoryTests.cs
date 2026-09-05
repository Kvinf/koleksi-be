using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Common;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// <c>DynamoAuctionRepository</c> against a real table.
/// </summary>
/// <remarks>
/// The only genuinely contended write in the product, and the only one that spans more
/// than one item. Both properties are what these tests are for: the version condition has
/// to be enforced by DynamoDB rather than by us, and the auction and its history rows have
/// to land together or not at all.
/// </remarks>
public sealed class AuctionRepositoryTests : DynamoTestBase
{
    [DynamoLocalFact]
    public async Task An_auction_survives_the_round_trip()
    {
        var auction = await SeedAuctionAsync();

        var expectedVersion = auction.Version;
        var outcome = auction.PlaceBid(
            Fixtures.BidderId, Fixtures.BidderUsername, Money.FromRupiah(4_000_000), Fixtures.Clock);
        await Table.Auctions.SaveBidAsync(auction, outcome.Bids, expectedVersion, Ct);

        var stored = await Table.Auctions.FindAsync(auction.ListingId, Ct);

        stored.ShouldNotBeNull();
        stored.ListingId.ShouldBe(auction.ListingId);
        stored.SellerId.ShouldBe(auction.SellerId);
        stored.StartingBid.Rupiah.ShouldBe(auction.StartingBid.Rupiah);
        stored.CurrentPrice.Rupiah.ShouldBe(auction.CurrentPrice.Rupiah);
        stored.BidCount.ShouldBe(auction.BidCount);
        stored.StartsAt.ShouldBe(auction.StartsAt);
        stored.EndsAt.ShouldBe(auction.EndsAt);
        stored.Status.ShouldBe(auction.Status);
        stored.LeadingBidderId.ShouldBe(Fixtures.BidderId);
        stored.Version.ShouldBe(auction.Version);
    }

    [DynamoLocalFact]
    public async Task A_bid_writes_the_auction_and_its_history_together()
    {
        var auction = await SeedAuctionAsync();

        var expectedVersion = auction.Version;
        var outcome = auction.PlaceBid(
            Fixtures.BidderId, Fixtures.BidderUsername, Money.FromRupiah(4_000_000), Fixtures.Clock);

        await Table.Auctions.SaveBidAsync(auction, outcome.Bids, expectedVersion, Ct);

        var history = await Table.Auctions.GetBidHistoryAsync(auction.ListingId, 25, Ct);
        history.Count.ShouldBe(outcome.Bids.Count);

        var stored = await Table.Auctions.FindAsync(auction.ListingId, Ct);
        stored.ShouldNotBeNull();
        stored.BidCount.ShouldBe(history.Count);
    }

    [DynamoLocalFact]
    public async Task A_bid_that_lost_the_race_writes_nothing_at_all()
    {
        // The reason this is a transaction. An auction whose price moved but whose history
        // row was lost is an auction where the winner is charged an amount no bid accounts
        // for — which is exactly what a losing bidder disputes.
        var auction = await SeedAuctionAsync();

        var staleVersion = auction.Version;

        var winner = await Table.Auctions.FindAsync(auction.ListingId, Ct);
        winner.ShouldNotBeNull();
        var winnerOutcome = winner.PlaceBid(
            Fixtures.BidderId, Fixtures.BidderUsername, Money.FromRupiah(4_000_000), Fixtures.Clock);
        await Table.Auctions.SaveBidAsync(winner, winnerOutcome.Bids, staleVersion, Ct);

        var loserOutcome = auction.PlaceBid(
            Fixtures.RivalId, Fixtures.RivalUsername, Money.FromRupiah(2_000_000), Fixtures.Clock);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Table.Auctions.SaveBidAsync(auction, loserOutcome.Bids, staleVersion, Ct));

        var history = await Table.Auctions.GetBidHistoryAsync(auction.ListingId, 25, Ct);
        history.ShouldAllBe(bid => bid.BidderId == Fixtures.BidderId);

        var stored = await Table.Auctions.FindAsync(auction.ListingId, Ct);
        stored.ShouldNotBeNull();
        stored.CurrentPrice.Rupiah.ShouldBe(winner.CurrentPrice.Rupiah);
        stored.LeadingBidderId.ShouldBe(Fixtures.BidderId);
    }

    [DynamoLocalFact]
    public async Task Bid_history_reads_back_newest_first_and_honours_its_limit()
    {
        var auction = await SeedAuctionAsync();

        var bidders = new[]
        {
            (Fixtures.BidderId, Fixtures.BidderUsername, 2_000_000L),
            (Fixtures.RivalId, Fixtures.RivalUsername, 3_000_000L),
            (Fixtures.BidderId, Fixtures.BidderUsername, 5_000_000L),
        };

        foreach (var (bidderId, username, maximum) in bidders)
        {
            var expectedVersion = auction.Version;
            var outcome = auction.PlaceBid(bidderId, username, Money.FromRupiah(maximum), Fixtures.Clock);
            await Table.Auctions.SaveBidAsync(auction, outcome.Bids, expectedVersion, Ct);
        }

        var all = await Table.Auctions.GetBidHistoryAsync(auction.ListingId, 25, Ct);
        all.Count.ShouldBe(auction.BidCount);

        // Bid ids are UUIDv7 in Crockford base-32 and double as the sort key, so newest
        // first is a descending key scan with no timestamp attribute involved.
        var ids = all.Select(bid => bid.Id).ToList();
        ids.ShouldBe(ids.OrderByDescending(id => id, StringComparer.Ordinal).ToList());

        var limited = await Table.Auctions.GetBidHistoryAsync(auction.ListingId, 2, Ct);
        limited.Count.ShouldBe(2);
        limited[0].Id.ShouldBe(ids[0]);
    }

    [DynamoLocalFact]
    public async Task An_auction_history_row_carries_the_bidder_and_amount()
    {
        var auction = await SeedAuctionAsync();

        var first = auction.Version;
        var opening = auction.PlaceBid(
            Fixtures.BidderId, Fixtures.BidderUsername, Money.FromRupiah(4_000_000), Fixtures.Clock);
        await Table.Auctions.SaveBidAsync(auction, opening.Bids, first, Ct);

        var second = auction.Version;
        var raise = auction.PlaceBid(
            Fixtures.RivalId, Fixtures.RivalUsername, Money.FromRupiah(4_500_000), Fixtures.Clock);
        await Table.Auctions.SaveBidAsync(auction, raise.Bids, second, Ct);

        var history = await Table.Auctions.GetBidHistoryAsync(auction.ListingId, 25, Ct);

        history.ShouldContain(bid => bid.BidderId == Fixtures.BidderId && bid.Amount.Rupiah == 4_000_000);
        history.ShouldContain(bid => bid.BidderId == Fixtures.RivalId && bid.Amount.Rupiah == 4_500_000);
    }

    [DynamoLocalFact]
    public async Task The_close_sweep_finds_auctions_across_every_shard()
    {
        // Four shards exist so bidding does not funnel through one partition. The sweep
        // has to query all of them, or auctions in three shards never close.
        //
        // Seeded until every shard is occupied rather than a fixed count: ids are random
        // below the timestamp, so a fixed count would leave a small chance of a run where
        // one shard is empty and the test passes without proving the fan-out.
        var perShard = new Dictionary<int, int>();
        var seeded = new List<Auction>();

        for (var i = 0; i < 200 && (perShard.Count < DynamoTable.AuctionShardCount
            || perShard.Values.Any(count => count < 2)); i++)
        {
            var listing = Fixtures.ActiveListing($"Kartu Lelang Berakhir Nomor Urut {i:D3}");
            var auction = Fixtures.LiveAuction(listing, duration: TimeSpan.FromHours(1 + (i % 24)));

            await Table.Auctions.AddAsync(auction, Ct);
            seeded.Add(auction);

            var shard = DynamoTable.ShardOf(auction.ListingId);
            perShard[shard] = perShard.GetValueOrDefault(shard) + 1;
        }

        perShard.Count.ShouldBe(DynamoTable.AuctionShardCount);

        var asOf = seeded.Max(auction => auction.EndsAt);
        var due = await Table.Auctions.ListDueForCloseAsync(asOf, limit: 100, Ct);

        due.Count.ShouldBe(seeded.Count);
    }

    [DynamoLocalFact]
    public async Task An_auction_ending_on_exactly_this_tick_is_swept()
    {
        // The sort key is "{instant}#{listingId}", so a bound of the bare instant excludes
        // every auction closing at exactly that moment — the set the sweep exists to find.
        var listing = Fixtures.ActiveListing("Kartu Lelang Berakhir Tepat Pada Detik Ini");
        var auction = Fixtures.LiveAuction(listing, duration: TimeSpan.FromHours(2));
        await Table.Auctions.AddAsync(auction, Ct);

        var due = await Table.Auctions.ListDueForCloseAsync(auction.EndsAt, limit: 10, Ct);

        due.ShouldContain(swept => swept.ListingId == auction.ListingId);
    }

    [DynamoLocalFact]
    public async Task An_auction_that_has_not_ended_is_not_swept()
    {
        var listing = Fixtures.ActiveListing("Kartu Lelang Masih Berjalan Beberapa Hari");
        var auction = Fixtures.LiveAuction(listing, duration: TimeSpan.FromDays(2));
        await Table.Auctions.AddAsync(auction, Ct);

        var due = await Table.Auctions.ListDueForCloseAsync(
            auction.EndsAt - TimeSpan.FromSeconds(1), limit: 10, Ct);

        due.ShouldNotContain(swept => swept.ListingId == auction.ListingId);
    }

    [DynamoLocalFact]
    public async Task A_swept_auction_arrives_complete_rather_than_half_populated()
    {
        // gsi_auction_end must project ALL. The sweep rehydrates a whole aggregate from
        // what the index returns, so a narrowed projection would hand back an auction with
        // a zeroed price — and close it at that price, with no error anywhere.
        var listing = Fixtures.ActiveListing("Kartu Lelang Dengan Penawaran Tertinggi Aktif");
        var auction = Fixtures.LiveAuction(listing, startingBidRupiah: 1_500_000);
        await Table.Auctions.AddAsync(auction, Ct);

        var expectedVersion = auction.Version;
        var outcome = auction.PlaceBid(
            Fixtures.BidderId, Fixtures.BidderUsername, Money.FromRupiah(6_000_000), Fixtures.Clock);
        await Table.Auctions.SaveBidAsync(auction, outcome.Bids, expectedVersion, Ct);

        var due = await Table.Auctions.ListDueForCloseAsync(auction.EndsAt, limit: 10, Ct);
        var swept = due.ShouldHaveSingleItem();

        swept.CurrentPrice.Rupiah.ShouldBe(auction.CurrentPrice.Rupiah);
        swept.CurrentPrice.Rupiah.ShouldNotBe(0);
        swept.SellerId.ShouldBe(auction.SellerId);
        swept.StartingBid.Rupiah.ShouldBe(1_500_000);
        swept.LeadingBidderId.ShouldBe(Fixtures.BidderId);
        swept.Version.ShouldBe(auction.Version);
    }

    [DynamoLocalFact]
    public async Task A_closed_auction_drops_out_of_the_sweep_index()
    {
        // The index is sparse: closing an auction removes its key attributes rather than
        // leaving a tombstone the sweep reads and skips forever.
        var listing = Fixtures.ActiveListing("Kartu Lelang Yang Sudah Selesai Dan Ditutup");
        var auction = Fixtures.LiveAuction(listing, duration: TimeSpan.FromHours(2));
        await Table.Auctions.AddAsync(auction, Ct);

        var clock = new FixedClock { UtcNow = auction.EndsAt.AddMinutes(1) };
        var expectedVersion = auction.Version;
        auction.Close(clock);
        await Table.Auctions.SaveBidAsync(auction, [], expectedVersion, Ct);

        var due = await Table.Auctions.ListDueForCloseAsync(clock.UtcNow, limit: 10, Ct);

        due.ShouldNotContain(swept => swept.ListingId == auction.ListingId);

        // Still readable by primary key — only the index entry is gone.
        (await Table.Auctions.FindAsync(auction.ListingId, Ct)).ShouldNotBeNull();
    }

    [DynamoLocalFact]
    public async Task Adding_a_second_auction_for_the_same_listing_is_refused()
    {
        var auction = await SeedAuctionAsync();

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Table.Auctions.AddAsync(auction, Ct));
    }

    private async Task<Auction> SeedAuctionAsync()
    {
        var listing = Fixtures.ActiveListing($"Kartu Lelang Uji Coba {EntityId.New()}");
        var auction = Fixtures.LiveAuction(listing);

        await Table.Auctions.AddAsync(auction, Ct);
        return auction;
    }
}
