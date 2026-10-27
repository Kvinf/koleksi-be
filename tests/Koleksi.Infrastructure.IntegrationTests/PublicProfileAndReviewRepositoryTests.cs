using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Reviews;
using Koleksi.Domain.Users;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// Public profiles, handle claims and reviews against a real table: the guarantees that exist only
/// as conditions on transactional writes.
/// </summary>
public sealed class PublicProfileAndReviewRepositoryTests : DynamoTestBase
{
    private const string Buyer = Fixtures.BidderId;
    private const string Seller = Fixtures.SellerId;

    private static PublicProfile ProfileFor(string userId, string handle) =>
        PublicProfile.Create(userId, handle, Fixtures.Clock.UtcNow, Fixtures.Clock);

    private static Order DeliveredOrder(string buyerId = Buyer)
    {
        var order = Order.Create(
            buyerId,
            Seller,
            [new OrderLine("01JQ8ZC4K7N2M5V9XR3P6TW0YB", "PSA 10 Charizard", null, Money.FromRupiah(2_500_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(15_000), 3),
            Fixtures.Clock);
        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);
        order.MarkShipped(
            Shipment.Create("jne", "JNE0012345678", "p/packed.jpg", "p/label.jpg", new DateOnly(2026, 1, 1)),
            Seller,
            Fixtures.Clock);
        order.MarkDelivered(Fixtures.Clock);
        return order;
    }

    [DynamoLocalFact]
    public async Task A_created_profile_is_found_by_account_and_by_handle()
    {
        var result = await Table.PublicProfiles.CreateAsync(ProfileFor(Seller, "budi"), Ct);

        result.ShouldBe(PublicProfileWriteResult.Written);
        (await Table.PublicProfiles.FindByUserAsync(Seller, Ct))!.Handle.ShouldBe("budi");
        (await Table.PublicProfiles.FindByHandleAsync("budi", Ct))!.UserId.ShouldBe(Seller);
        (await Table.PublicProfiles.FindByHandleAsync("tidak-ada", Ct)).ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task Two_accounts_racing_for_one_handle_cannot_both_win()
    {
        var accounts = Enumerable.Range(0, 8).Select(i => $"account-{i}").ToList();

        var results = await Task.WhenAll(accounts.Select(
            account => Table.PublicProfiles.CreateAsync(ProfileFor(account, "rebutan"), Ct)));

        results.Count(r => r == PublicProfileWriteResult.Written).ShouldBe(1);
        results.Count(r => r == PublicProfileWriteResult.HandleTaken).ShouldBe(accounts.Count - 1);

        var winner = await Table.PublicProfiles.FindByHandleAsync("rebutan", Ct);
        winner.ShouldNotBeNull();

        // The losers left nothing behind: no profile without a claim.
        foreach (var account in accounts.Where(a => a != winner.UserId))
        {
            (await Table.PublicProfiles.FindByUserAsync(account, Ct)).ShouldBeNull();
        }
    }

    [DynamoLocalFact]
    public async Task An_account_cannot_be_given_a_second_profile()
    {
        await Table.PublicProfiles.CreateAsync(ProfileFor(Seller, "budi"), Ct);

        var second = await Table.PublicProfiles.CreateAsync(ProfileFor(Seller, "budi-lain"), Ct);

        second.ShouldBe(PublicProfileWriteResult.AlreadyExists);

        // And the failed attempt did not claim the other handle.
        (await Table.PublicProfiles.FindByHandleAsync("budi-lain", Ct)).ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task Renaming_moves_the_claim_and_reserves_the_old_handle_for_its_owner()
    {
        await Table.PublicProfiles.CreateAsync(ProfileFor(Seller, "budi"), Ct);
        var profile = (await Table.PublicProfiles.FindByUserAsync(Seller, Ct))!;
        var expected = profile.Version;
        profile.Edit("budi-baru", "Toko Budi", Fixtures.Clock);

        var result = await Table.PublicProfiles.SaveAsync(profile, "budi", expected, Ct);

        result.ShouldBe(PublicProfileWriteResult.Written);
        (await Table.PublicProfiles.FindByHandleAsync("budi", Ct)).ShouldBeNull();
        (await Table.PublicProfiles.FindByHandleAsync("budi-baru", Ct))!.DisplayName.ShouldBe("Toko Budi");

        // The old handle no longer resolves to a page, but it is held back (QA D-10): nobody else
        // can take the old shop URL while the reservation lasts.
        (await Table.PublicProfiles.CreateAsync(ProfileFor(Buyer, "budi"), Ct)).ShouldBe(PublicProfileWriteResult.HandleTaken);

        // Once the reservation has lapsed, anyone can claim it.
        var afterLapse = new FixedClock(Fixtures.Clock.UtcNow + PublicProfile.HandleReservationPeriod + TimeSpan.FromDays(1));
        var latecomer = PublicProfile.Create(Buyer, "budi", afterLapse.UtcNow, afterLapse);
        (await Table.PublicProfiles.CreateAsync(latecomer, Ct)).ShouldBe(PublicProfileWriteResult.Written);
        (await Table.PublicProfiles.FindByHandleAsync("budi", Ct))!.UserId.ShouldBe(Buyer);
    }

    [DynamoLocalFact]
    public async Task Renaming_onto_a_taken_handle_changes_nothing()
    {
        await Table.PublicProfiles.CreateAsync(ProfileFor(Seller, "budi"), Ct);
        await Table.PublicProfiles.CreateAsync(ProfileFor(Buyer, "rangga"), Ct);
        var profile = (await Table.PublicProfiles.FindByUserAsync(Seller, Ct))!;
        var expected = profile.Version;
        profile.Edit("rangga", null, Fixtures.Clock);

        var result = await Table.PublicProfiles.SaveAsync(profile, "budi", expected, Ct);

        result.ShouldBe(PublicProfileWriteResult.HandleTaken);
        (await Table.PublicProfiles.FindByUserAsync(Seller, Ct))!.Handle.ShouldBe("budi");
        (await Table.PublicProfiles.FindByHandleAsync("budi", Ct))!.UserId.ShouldBe(Seller);
        (await Table.PublicProfiles.FindByHandleAsync("rangga", Ct))!.UserId.ShouldBe(Buyer);
    }

    [DynamoLocalFact]
    public async Task A_stale_save_is_a_concurrency_conflict()
    {
        await Table.PublicProfiles.CreateAsync(ProfileFor(Seller, "budi"), Ct);
        var first = (await Table.PublicProfiles.FindByUserAsync(Seller, Ct))!;
        var second = (await Table.PublicProfiles.FindByUserAsync(Seller, Ct))!;
        var version = first.Version;
        first.Edit("budi", "Satu", Fixtures.Clock);
        second.Edit("budi", "Dua", Fixtures.Clock);

        await Table.PublicProfiles.SaveAsync(first, "budi", version, Ct);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Table.PublicProfiles.SaveAsync(second, "budi", version, Ct));
        (await Table.PublicProfiles.FindByUserAsync(Seller, Ct))!.DisplayName.ShouldBe("Satu");
    }

    [DynamoLocalFact]
    public async Task Many_accounts_resolve_in_one_call_and_unknown_ones_are_absent()
    {
        await Table.PublicProfiles.CreateAsync(ProfileFor(Seller, "budi"), Ct);
        await Table.PublicProfiles.CreateAsync(ProfileFor(Buyer, "rangga"), Ct);

        var found = await Table.PublicProfiles.FindManyByUserAsync([Seller, Buyer, "nobody"], Ct);

        found.Keys.ShouldBe([Seller, Buyer], ignoreOrder: true);
        found[Buyer].Handle.ShouldBe("rangga");
    }

    [DynamoLocalFact]
    public async Task Two_submissions_for_one_order_leave_exactly_one_review_and_one_count()
    {
        var order = DeliveredOrder();
        var attempts = Enumerable.Range(0, 6)
            .Select(i => Review.Create(order, Buyer, 1 + (i % 5), $"percobaan {i}", false, Fixtures.Clock))
            .ToList();

        var results = await Task.WhenAll(attempts.Select(review => Table.Reviews.TryAddAsync(review, Ct)));

        results.Count(added => added).ShouldBe(1);

        var stats = await Table.Reviews.GetStatsAsync(Seller, Ct);
        stats.Count.ShouldBe(1);

        var stored = await Table.Reviews.FindByOrderAsync(order.Id, Ct);
        stored.ShouldNotBeNull();
        stats.Sum.ShouldBe(stored.Rating);
        stats.Histogram[stored.Rating - 1].ShouldBe(1);

        var feed = await Table.Reviews.ListBySellerAsync(Seller, null, 10, Ct);
        feed.Items.ShouldHaveSingleItem().Id.ShouldBe(stored.Id);
    }

    [DynamoLocalFact]
    public async Task Counters_add_up_across_orders_even_when_reviews_land_together()
    {
        var reviews = Enumerable.Range(0, 10)
            .Select(i => Review.Create(DeliveredOrder(), Buyer, 1 + (i % 5), null, i % 2 == 0, Fixtures.Clock))
            .ToList();

        await Task.WhenAll(reviews.Select(review => Table.Reviews.TryAddAsync(review, Ct)));

        var stats = await Table.Reviews.GetStatsAsync(Seller, Ct);
        stats.Count.ShouldBe(10);
        stats.Sum.ShouldBe(reviews.Sum(r => (long)r.Rating));
        stats.Histogram.ShouldBe([2L, 2L, 2L, 2L, 2L]);
    }

    [DynamoLocalFact]
    public async Task The_feed_is_newest_first_and_pages_with_a_cursor_without_including_the_counters()
    {
        for (var i = 0; i < 5; i++)
        {
            var clock = new FixedClock(Fixtures.Clock.UtcNow.AddMinutes(i));
            var review = Review.Create(DeliveredOrder(), Buyer, 5, $"ulasan {i}", false, clock);
            await Table.Reviews.TryAddAsync(review, Ct);
        }

        var first = await Table.Reviews.ListBySellerAsync(Seller, null, 3, Ct);
        first.Items.Select(r => r.Text).ShouldBe(["ulasan 4", "ulasan 3", "ulasan 2"]);
        first.NextCursor.ShouldNotBeNull();

        var second = await Table.Reviews.ListBySellerAsync(Seller, first.NextCursor, 3, Ct);
        second.Items.Select(r => r.Text).ShouldBe(["ulasan 1", "ulasan 0"]);
    }

    [DynamoLocalFact]
    public async Task A_seller_with_no_reviews_has_empty_counters_and_an_order_without_one_has_no_review()
    {
        (await Table.Reviews.GetStatsAsync(Seller, Ct)).Count.ShouldBe(0);
        (await Table.Reviews.FindByOrderAsync(DeliveredOrder().Id, Ct)).ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_review_never_appears_in_the_buyers_or_sellers_order_lists()
    {
        var order = DeliveredOrder();
        await Table.Orders.AddManyAsync([order], Ct);
        await Table.Reviews.TryAddAsync(Review.Create(order, Buyer, 5, "Mantap", false, Fixtures.Clock), Ct);

        (await Table.Orders.ListForBuyerAsync(Buyer, null, 20, Ct)).Items.ShouldHaveSingleItem().Id.ShouldBe(order.Id);
        (await Table.Orders.ListForSellerAsync(Seller, null, 20, Ct)).Items.ShouldHaveSingleItem().Id.ShouldBe(order.Id);
        (await Table.Orders.FindAsync(order.Id, Ct)).ShouldNotBeNull();
    }

    private sealed class FixedClock(DateTimeOffset instant) : IClock
    {
        public DateTimeOffset UtcNow { get; } = instant;
    }
}
