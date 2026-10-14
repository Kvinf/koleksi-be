using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>Key composition for a person's saved listings.</summary>
public sealed class WatchlistKeyTests
{
    private const string UserId = "b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33";
    private const string ListingId = "01JQ8ZC4K7N2M5V9XR3P6TW0YB";

    [Fact]
    public void A_persons_saves_share_one_partition_with_one_row_per_listing()
    {
        DynamoTable.WatchlistPk(UserId).ShouldBe("WATCH#" + UserId);
        DynamoTable.WatchlistItemSk(ListingId).ShouldBe("LISTING#" + ListingId);
        DynamoTable.WatchlistItemSk(ListingId).ShouldStartWith(DynamoTable.WatchlistItemSkPrefix());
    }

    [Fact]
    public void The_watchlist_partition_is_not_the_cart_partition()
    {
        // A cart read must never page through saved listings, nor the reverse.
        DynamoTable.WatchlistPk(UserId).ShouldNotBe(DynamoTable.CartPk(UserId));
    }

    [Fact]
    public void The_row_is_never_in_the_listings_own_partition()
    {
        // The counter lives on the listing row; the saved row lives with the person.
        DynamoTable.WatchlistPk(UserId).ShouldNotBe(DynamoTable.ListingPk(ListingId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_components_are_refused(string blank)
    {
        Should.Throw<ArgumentException>(() => DynamoTable.WatchlistPk(blank));
        Should.Throw<ArgumentException>(() => DynamoTable.WatchlistItemSk(blank));
    }
}
