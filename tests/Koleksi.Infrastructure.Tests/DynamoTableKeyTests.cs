using System.Globalization;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// Key composition.
/// </summary>
/// <remarks>
/// Keys are the one thing in this codebase that cannot be fixed forward. A repository
/// that writes an item under a key nothing reads back leaves no error behind, and a
/// change to how a key is built silently orphans everything written before it. These
/// tests exist to make such a change fail loudly.
/// </remarks>
public sealed class DynamoTableKeyTests
{
    private const string ListingId = "01JQ8ZC4K7N2M5V9XR3P6TW0YB";

    [Fact]
    public void Everything_about_one_listing_shares_a_partition()
    {
        // This is what lets a bid and the auction it changes be written in one
        // transaction. DynamoDB transactions span partitions, but the single-partition
        // read that renders the item page does not.
        var partition = DynamoTable.ListingPk(ListingId);

        partition.ShouldBe("LISTING#" + ListingId);
        DynamoTable.ListingMetaSk().ShouldBe("#META");
        DynamoTable.AuctionSk().ShouldBe("#AUCTION");
        DynamoTable.BrowseSk().ShouldBe("#BROWSE");
        DynamoTable.BidSk("01JQ8ZC4K7N2M5V9XR3P6TW0YC").ShouldStartWith(DynamoTable.BidSkPrefix());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_key_component_is_refused(string component)
    {
        // An empty component produces a key like "LISTING#" that collides with every
        // other empty one, so a null id would quietly write over a shared row.
        Should.Throw<ArgumentException>(() => DynamoTable.ListingPk(component));
        Should.Throw<ArgumentException>(() => DynamoTable.BrowsePkValue(component));
        Should.Throw<ArgumentException>(() => DynamoTable.SellerPkValue(component));
    }

    [Fact]
    public void Prices_sort_by_value_rather_than_by_digit_count()
    {
        // DynamoDB sorts strings lexicographically. Unpadded, "9000" sorts after
        // "10000000", so the cheapest-first page would open with the most expensive lot.
        var cheap = DynamoTable.BrowsePriceSkValue(9_000, ListingId);
        var dear = DynamoTable.BrowsePriceSkValue(10_000_000, ListingId);

        string.CompareOrdinal(cheap, dear).ShouldBeLessThan(0);
    }

    [Fact]
    public void A_negative_price_is_not_a_key()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => DynamoTable.BrowsePriceSkValue(-1, ListingId));
    }

    [Fact]
    public void The_seller_index_lets_a_dashboard_tab_be_a_prefix_query()
    {
        // Status leads the sort key so "Active" is a key condition rather than a filter
        // over every listing the seller has ever created.
        var key = DynamoTable.SellerSkValue("Active", ListingId);

        key.ShouldStartWith(DynamoTable.SellerSkPrefix("Active"));
        key.ShouldNotStartWith(DynamoTable.SellerSkPrefix("Draft"));
    }

    [Fact]
    public void Close_times_sort_by_instant()
    {
        var earlier = DynamoTable.AuctionEndSkValue(
            new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero), ListingId);

        var later = DynamoTable.AuctionEndSkValue(
            new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero), ListingId);

        string.CompareOrdinal(earlier, later).ShouldBeLessThan(0);
    }

    [Fact]
    public void An_instant_expressed_in_another_offset_produces_the_same_key()
    {
        // Auctions close for Indonesian sellers, whose wall clock is UTC+7. If the offset
        // leaked into the key, the same instant would sort in two different places.
        var utc = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var jakarta = new DateTimeOffset(2026, 3, 1, 16, 0, 0, TimeSpan.FromHours(7));

        DynamoTable.AuctionEndSkValue(jakarta, ListingId)
            .ShouldBe(DynamoTable.AuctionEndSkValue(utc, ListingId));
    }

    [Fact]
    public void The_close_sweeps_upper_bound_includes_auctions_ending_on_the_tick()
    {
        // The sort key appends an id after the instant, so a bound of the bare instant
        // would exclude every auction closing at exactly that moment — which is precisely
        // the set the sweep exists to find.
        var instant = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        var onTheTick = DynamoTable.AuctionEndSkValue(instant, ListingId);
        var bound = DynamoTable.EndsAtUpperBound(instant);

        string.CompareOrdinal(onTheTick, bound).ShouldBeLessThan(0);
    }

    [Fact]
    public void An_instant_round_trips_through_its_key_form()
    {
        var instant = new DateTimeOffset(2026, 3, 1, 9, 17, 42, 123, TimeSpan.Zero);

        DynamoTable.ParseInstant(DynamoTable.FormatInstant(instant)).ShouldBe(instant);
    }

    [Fact]
    public void Idempotency_keys_are_scoped_so_one_caller_cannot_claim_anothers()
    {
        // The scope is part of the partition key. A global key namespace would let anyone
        // suppress someone else's checkout by guessing the key it used.
        var mine = DynamoTable.IdempotencyPkValue("checkout:me", "abc123");
        var theirs = DynamoTable.IdempotencyPkValue("checkout:you", "abc123");

        mine.ShouldNotBe(theirs);
    }

    [Fact]
    public void The_ttl_attribute_is_unix_seconds()
    {
        // DynamoDB's TTL feature accepts nothing else, and silently never expires an item
        // stored in any other unit.
        var expiresAt = DateTimeOffset.UnixEpoch.AddSeconds(1_800_000_000);

        DynamoTable.ToTtl(expiresAt).ShouldBe(1_800_000_000);
    }

    [Fact]
    public void A_price_key_is_the_same_under_any_ambient_culture()
    {
        // InvariantGlobalization means the deployed runtime has no ICU at all, so a key
        // built with an ambient culture separator locally would not match one built in
        // Lambda. Every conversion here is explicitly invariant.
        var key = DynamoTable.BrowsePriceSkValue(1_250_000, ListingId);

        key.ShouldNotContain(",");
        key.ShouldNotContain(".");
        key.ShouldStartWith(1_250_000.ToString(CultureInfo.InvariantCulture).PadLeft(16, '0'));
    }
}
