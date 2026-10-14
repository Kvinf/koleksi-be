using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary><c>DynamoOrderRepository</c> against a real table.</summary>
public sealed class OrderRepositoryTests : DynamoTestBase
{
    [DynamoLocalFact]
    public async Task An_order_survives_the_round_trip()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);

        await Table.Orders.AddManyAsync([order], Ct);
        var stored = await Table.Orders.FindAsync(order.Id, Ct);

        stored.ShouldNotBeNull();
        stored.OrderNumber.ShouldBe(order.OrderNumber);
        stored.BuyerId.ShouldBe(order.BuyerId);
        stored.SellerId.ShouldBe(order.SellerId);
        stored.Lines.Count.ShouldBe(1);
        stored.Lines[0].UnitPrice.Rupiah.ShouldBe(order.Lines[0].UnitPrice.Rupiah);
        stored.ShipTo.RecipientName.ShouldBe(order.ShipTo.RecipientName);
        stored.Courier.CourierCode.ShouldBe(order.Courier.CourierCode);
        stored.EscrowState.ShouldBe(EscrowState.AwaitingPayment);
    }

    [DynamoLocalFact]
    public async Task An_order_is_findable_by_its_printed_order_number()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        var stored = await Table.Orders.FindByOrderNumberAsync(order.OrderNumber, Ct);

        stored.ShouldNotBeNull();
        stored.Id.ShouldBe(order.Id);
    }

    [DynamoLocalFact]
    public async Task An_unknown_order_number_resolves_to_nothing()
    {
        var stored = await Table.Orders.FindByOrderNumberAsync("KLK-000000", Ct);

        stored.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_checkout_spanning_two_shops_writes_both_orders_atomically()
    {
        var listingOne = Fixtures.ActiveListing(title: "PSA 10 Charizard Base Set Holo 1999");
        var listingTwo = Fixtures.ActiveListing(title: "Sealed Gunpla RG Nu Gundam Ver Ka", priceRupiah: 1_800_000);
        var orderOne = Fixtures.SampleOrder(listingOne);
        var orderTwo = Fixtures.SampleOrder(listingTwo);

        await Table.Orders.AddManyAsync([orderOne, orderTwo], Ct);

        (await Table.Orders.FindAsync(orderOne.Id, Ct)).ShouldNotBeNull();
        (await Table.Orders.FindAsync(orderTwo.Id, Ct)).ShouldNotBeNull();
    }

    [DynamoLocalFact]
    public async Task Confirming_payment_persists_through_an_update()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);
        await Table.Orders.UpdateAsync(order, expectedVersion: 0, Ct);

        var stored = await Table.Orders.FindAsync(order.Id, Ct);
        stored.ShouldNotBeNull();
        stored.EscrowState.ShouldBe(EscrowState.Paid);
        stored.PaidAt.ShouldNotBeNull();
    }

    [DynamoLocalFact]
    public async Task A_stale_version_update_is_rejected()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Table.Orders.UpdateAsync(order, expectedVersion: 41, Ct));
    }

    [DynamoLocalFact]
    public async Task Orders_list_for_both_the_buyer_and_the_seller()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        var forSeller = await Table.Orders.ListForSellerAsync(listing.SellerId, null, 10, Ct);
        var forBuyer = await Table.Orders.ListForBuyerAsync(Fixtures.BidderId, null, 10, Ct);

        forSeller.Items.ShouldHaveSingleItem().Id.ShouldBe(order.Id);
        forBuyer.Items.ShouldHaveSingleItem().Id.ShouldBe(order.Id);
    }

    [DynamoLocalFact]
    public async Task A_delivered_order_past_its_auto_release_deadline_is_due()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);
        order.MarkShipped(
            Shipment.Create("jne", "JNE1234567890", "photo/parcel.jpg", "photo/label.jpg",
                DateOnly.FromDateTime(Fixtures.Clock.UtcNow.UtcDateTime)),
            listing.SellerId, Fixtures.Clock);
        order.MarkDelivered(Fixtures.Clock);

        // The stored row is still at whatever AddManyAsync wrote (version 0) — none of the
        // in-memory mutations above have been persisted yet, only accumulated on the
        // aggregate. One UpdateAsync writes and validates against that stored version.
        await Table.Orders.UpdateAsync(order, expectedVersion: 0, Ct);

        var due = await Table.Orders.ListDueForAutoReleaseAsync(
            Fixtures.Clock.UtcNow + Order.AutoReleaseAfterDelivery + TimeSpan.FromMinutes(1), 10, Ct);

        due.ShouldContain(candidate => candidate.Id == order.Id);
    }

    [DynamoLocalFact]
    public async Task An_unpaid_order_is_due_for_cancellation_only_once_its_window_has_elapsed()
    {
        var order = Fixtures.SampleOrder(Fixtures.ActiveListing());
        await Table.Orders.AddManyAsync([order], Ct);

        var windowEnds = order.CreatedAt + Order.PaymentWindow;

        (await Table.Orders.ListPaymentExpiredAsync(windowEnds - TimeSpan.FromSeconds(1), 10, Ct))
            .ShouldNotContain(candidate => candidate.Id == order.Id);

        // Inclusive of the instant itself (the tick boundary), like the auction sweep.
        (await Table.Orders.ListPaymentExpiredAsync(windowEnds, 10, Ct))
            .ShouldContain(candidate => candidate.Id == order.Id);
    }

    [DynamoLocalFact]
    public async Task A_paid_order_drops_out_of_the_cancellation_sweep()
    {
        // The whole-item write that records payment carries no sweep key, which is what removes it.
        var order = Fixtures.SampleOrder(Fixtures.ActiveListing());
        await Table.Orders.AddManyAsync([order], Ct);

        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);
        await Table.Orders.UpdateAsync(order, expectedVersion: 0, Ct);

        var farFuture = order.CreatedAt + TimeSpan.FromDays(30);
        (await Table.Orders.ListPaymentExpiredAsync(farFuture, 10, Ct)).ShouldBeEmpty();
        (await Table.Orders.ListDueForAutoReleaseAsync(farFuture, 10, Ct)).ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task A_cancelled_order_drops_out_of_the_cancellation_sweep()
    {
        var order = Fixtures.SampleOrder(Fixtures.ActiveListing());
        await Table.Orders.AddManyAsync([order], Ct);

        order.Cancel(Fixtures.Clock);
        await Table.Orders.UpdateAsync(order, expectedVersion: 0, Ct);

        (await Table.Orders.ListPaymentExpiredAsync(order.CreatedAt + TimeSpan.FromDays(30), 10, Ct)).ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task The_two_order_sweeps_do_not_see_each_others_orders()
    {
        var unpaid = Fixtures.SampleOrder(Fixtures.ActiveListing("Kartu Belum Dibayar Nomor Satu Dua Tiga"));
        var delivered = Fixtures.SampleOrder(Fixtures.ActiveListing("Kartu Sudah Terkirim Nomor Empat Lima"));
        await Table.Orders.AddManyAsync([unpaid, delivered], Ct);

        delivered.ConfirmPayment(delivered.GrandTotal, Fixtures.Clock);
        delivered.MarkShipped(
            Shipment.Create("jne", "JNE1234567890", "photo/parcel.jpg", "photo/label.jpg",
                DateOnly.FromDateTime(Fixtures.Clock.UtcNow.UtcDateTime)),
            delivered.SellerId, Fixtures.Clock);
        delivered.MarkDelivered(Fixtures.Clock);
        await Table.Orders.UpdateAsync(delivered, expectedVersion: 0, Ct);

        var farFuture = Fixtures.Clock.UtcNow + TimeSpan.FromDays(30);

        (await Table.Orders.ListPaymentExpiredAsync(farFuture, 10, Ct)).ShouldHaveSingleItem().Id.ShouldBe(unpaid.Id);
        (await Table.Orders.ListDueForAutoReleaseAsync(farFuture, 10, Ct)).ShouldHaveSingleItem().Id.ShouldBe(delivered.Id);
    }

    [DynamoLocalFact]
    public async Task A_released_order_drops_out_of_the_release_sweep()
    {
        var order = Fixtures.SampleOrder(Fixtures.ActiveListing());
        await Table.Orders.AddManyAsync([order], Ct);

        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);
        order.MarkShipped(
            Shipment.Create("jne", "JNE1234567890", "photo/parcel.jpg", "photo/label.jpg",
                DateOnly.FromDateTime(Fixtures.Clock.UtcNow.UtcDateTime)),
            order.SellerId, Fixtures.Clock);
        order.MarkDelivered(Fixtures.Clock);
        await Table.Orders.UpdateAsync(order, expectedVersion: 0, Ct);

        var version = order.Version;
        order.Release(confirmedByBuyer: true, Fixtures.Clock);
        await Table.Orders.UpdateAsync(order, version, Ct);

        (await Table.Orders.ListDueForAutoReleaseAsync(Fixtures.Clock.UtcNow + TimeSpan.FromDays(30), 10, Ct))
            .ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task Orders_and_auctions_share_the_index_without_ever_meeting()
    {
        // Both ride gsi_auction_end. An order query that returned an auction row (or the reverse)
        // would fail to map, so succeeding with exactly the right rows is the assertion.
        var auction = Fixtures.LiveAuction(Fixtures.ActiveListing("Kartu Lelang Di Indeks Yang Sama"), duration: TimeSpan.FromHours(2));
        await Table.Auctions.AddAsync(auction, Ct);

        var order = Fixtures.SampleOrder(Fixtures.ActiveListing("Kartu Pesanan Di Indeks Yang Sama"));
        await Table.Orders.AddManyAsync([order], Ct);

        var farFuture = Fixtures.Clock.UtcNow + TimeSpan.FromDays(30);

        (await Table.Orders.ListPaymentExpiredAsync(farFuture, 10, Ct)).ShouldHaveSingleItem().Id.ShouldBe(order.Id);
        (await Table.Auctions.ListDueForCloseAsync(farFuture, 10, Ct)).ShouldHaveSingleItem().ListingId.ShouldBe(auction.ListingId);
    }

    [DynamoLocalFact]
    public async Task The_cancellation_sweep_reads_every_shard_and_honours_its_limit()
    {
        var seeded = new List<Order>();
        var shards = new HashSet<int>();
        for (var i = 0; i < 200 && shards.Count < DynamoTable.AuctionShardCount; i++)
        {
            var order = Fixtures.SampleOrder(Fixtures.ActiveListing($"Kartu Pesanan Lintas Shard Nomor {i:D3}"));
            seeded.Add(order);
            shards.Add(DynamoTable.ShardOf(order.Id));
        }

        shards.Count.ShouldBe(DynamoTable.AuctionShardCount);
        await Table.Orders.AddManyAsync(seeded.Take(25).ToList(), Ct);
        await Table.Orders.AddManyAsync(seeded.Skip(25).ToList(), Ct);

        var farFuture = Fixtures.Clock.UtcNow + TimeSpan.FromDays(30);

        (await Table.Orders.ListPaymentExpiredAsync(farFuture, 500, Ct)).Count.ShouldBe(seeded.Count);
        (await Table.Orders.ListPaymentExpiredAsync(farFuture, 4, Ct)).Count.ShouldBeLessThanOrEqualTo(4);
    }
}
