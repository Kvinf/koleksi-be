using Koleksi.Application.Abstractions;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary><c>DynamoListingHoldStore</c> against a real table.</summary>
/// <remarks>
/// The race a fake cannot vouch for: two buyers taking the same item at once must produce exactly
/// one winner, because the condition lives in the single conditional write.
/// </remarks>
public sealed class ListingHoldStoreTests : DynamoTestBase
{
    private const string Listing = "01JQ8ZC4K7N2M5V9XR3P6TW0YB";

    [DynamoLocalFact]
    public async Task The_first_buyer_gets_the_hold_and_a_second_buyer_is_refused()
    {
        (await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-a", Ct)).Acquired.ShouldBeTrue();
        (await Table.ListingHolds.TryHoldAsync(Listing, "buyer-b", "order-b", Ct)).Acquired.ShouldBeFalse();
    }

    [DynamoLocalFact]
    public async Task The_same_buyer_renews_the_hold_and_is_told_which_order_it_replaced()
    {
        await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-1", Ct);

        var again = await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-2", Ct);

        again.Acquired.ShouldBeTrue();
        again.ReplacedOrderId.ShouldBe("order-1");
    }

    [DynamoLocalFact]
    public async Task A_first_hold_lasts_the_normal_duration()
    {
        var first = await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-1", Ct);

        first.HeldUntil.ShouldBe(Table.Clock.UtcNow + IListingHoldStore.Duration);
    }

    [DynamoLocalFact]
    public async Task Renewing_cannot_extend_a_hold_past_the_cap_counted_from_its_first_acquisition()
    {
        await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-1", Ct);
        var firstAcquired = Table.Clock.UtcNow;

        // Ten minutes in, a renewal would normally run to minute 25: still inside the 30 cap.
        Table.Clock.UtcNow = firstAcquired + TimeSpan.FromMinutes(10);
        var second = await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-2", Ct);
        second.Acquired.ShouldBeTrue();
        second.HeldUntil.ShouldBe(firstAcquired + TimeSpan.FromMinutes(25));

        // Twenty-four minutes in, it would run to minute 39: the cap holds it at 30.
        Table.Clock.UtcNow = firstAcquired + TimeSpan.FromMinutes(24);
        var third = await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-3", Ct);
        third.Acquired.ShouldBeTrue();
        third.ReplacedOrderId.ShouldBe("order-2");
        third.HeldUntil.ShouldBe(firstAcquired + IListingHoldStore.MaxTotalDuration);

        // Once it has run out, another buyer can have it.
        Table.Clock.UtcNow = firstAcquired + IListingHoldStore.MaxTotalDuration + TimeSpan.FromSeconds(1);
        (await Table.ListingHolds.TryHoldAsync(Listing, "buyer-b", "order-b", Ct)).Acquired.ShouldBeTrue();
    }

    [DynamoLocalFact]
    public async Task A_lapsed_hold_taken_again_by_the_same_buyer_starts_a_fresh_count()
    {
        await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-1", Ct);

        Table.Clock.UtcNow += IListingHoldStore.Duration + TimeSpan.FromMinutes(1);
        var again = await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-2", Ct);

        again.Acquired.ShouldBeTrue();
        again.ReplacedOrderId.ShouldBeNull();
        again.HeldUntil.ShouldBe(Table.Clock.UtcNow + IListingHoldStore.Duration);
    }

    [DynamoLocalFact]
    public async Task Concurrent_buyers_produce_exactly_one_winner()
    {
        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Table.ListingHolds.TryHoldAsync(Listing, $"buyer-{i}", $"order-{i}", Ct)));

        attempts.Count(result => result.Acquired).ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task Releasing_frees_the_item_but_only_for_the_order_that_took_it()
    {
        await Table.ListingHolds.TryHoldAsync(Listing, "buyer-a", "order-a", Ct);

        await Table.ListingHolds.ReleaseAsync(Listing, "buyer-a", "some-other-order", Ct);
        (await Table.ListingHolds.TryHoldAsync(Listing, "buyer-b", "order-b", Ct)).Acquired.ShouldBeFalse();

        await Table.ListingHolds.ReleaseAsync(Listing, "buyer-a", "order-a", Ct);
        (await Table.ListingHolds.TryHoldAsync(Listing, "buyer-b", "order-b", Ct)).Acquired.ShouldBeTrue();
    }
}
