using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// The sweep's index entries on orders: present only while a clock is running, in a partition no
/// auction shares.
/// </summary>
/// <remarks>
/// Orders are always written as a whole item, so an attribute an order does not carry is removed
/// by that write. These tests are what say "carries" is true in exactly two states, because a
/// stale entry left behind is an order the sweep keeps finding (or, worse, mishandling).
/// </remarks>
public sealed class OrderDueKeyTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly CourierSelection Jne = new("jne", "JNE REG", Money.FromRupiah(24_000), 3);

    private static Order NewOrder(FixedClock clock) => Order.Create(
        Buyer,
        Seller,
        [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
        new ShippingAddress("Dwi Prasetyo", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
        Jne,
        clock);

    private static Order Paid(FixedClock clock)
    {
        var order = NewOrder(clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        return order;
    }

    private static Order Delivered(FixedClock clock)
    {
        var order = Paid(clock);
        order.MarkShipped(
            Shipment.Create("jne", "JNE0012345678", "proof/packed.jpg", "proof/label.jpg", new DateOnly(2026, 3, 1)),
            Seller,
            clock);
        order.MarkDelivered(clock);
        return order;
    }

    [Fact]
    public void An_unpaid_order_is_due_when_its_payment_window_ends()
    {
        var clock = new FixedClock(Start);
        var order = NewOrder(clock);

        var item = OrderItem.ToItem(order);

        item[DynamoTable.AuctionEndPk].S.ShouldBe(
            DynamoTable.OrderDuePkValue("AwaitingPayment", DynamoTable.ShardOf(order.Id)));
        item[DynamoTable.AuctionEndSk].S.ShouldBe(
            $"{DynamoTable.FormatInstant(Start + Order.PaymentWindow)}#{order.Id}");
    }

    [Fact]
    public void A_delivered_order_is_due_at_its_auto_release_time()
    {
        var clock = new FixedClock(Start);
        var order = Delivered(clock);

        var item = OrderItem.ToItem(order);

        item[DynamoTable.AuctionEndPk].S.ShouldBe(
            DynamoTable.OrderDuePkValue("Delivered", DynamoTable.ShardOf(order.Id)));
        item[DynamoTable.AuctionEndSk].S.ShouldBe(
            $"{DynamoTable.FormatInstant(Start + Order.AutoReleaseAfterDelivery)}#{order.Id}");
    }

    [Fact]
    public void A_shipped_parcel_is_due_for_a_courier_check_every_thirty_minutes_until_it_is_delivered()
    {
        var clock = new FixedClock(Start);
        var order = Paid(clock);
        order.MarkShipped(
            Shipment.Create("jne", "JNE0012345678", "proof/packed.jpg", "proof/label.jpg", new DateOnly(2026, 3, 1)),
            Seller,
            clock);

        var entry = OrderItem.ToItem(order);
        entry[DynamoTable.AuctionEndPk].S.ShouldBe(
            DynamoTable.OrderDuePkValue(OrderItem.TrackingState, DynamoTable.ShardOf(order.Id)));
        entry[DynamoTable.AuctionEndSk].S.ShouldBe(
            DynamoTable.OrderDueSkValue(Start + Order.TrackingPollInterval, order.Id));

        // Asked and not delivered yet: the next question waits another interval.
        clock.Advance(Order.TrackingPollInterval);
        order.RecordTrackingCheck(clock);
        OrderItem.ToItem(order)[DynamoTable.AuctionEndSk].S.ShouldBe(
            DynamoTable.OrderDueSkValue(Start + Order.TrackingPollInterval + Order.TrackingPollInterval, order.Id));

        // Delivered: it leaves the tracking partition and joins the auto-release one.
        order.MarkDelivered(clock);
        OrderItem.ToItem(order)[DynamoTable.AuctionEndPk].S.ShouldBe(
            DynamoTable.OrderDuePkValue(nameof(EscrowState.Delivered), DynamoTable.ShardOf(order.Id)));
    }

    [Fact]
    public void An_order_remembers_where_it_came_from_and_its_own_payment_deadline()
    {
        var clock = new FixedClock(Start);
        var due = Start + Order.AuctionPaymentWindow;
        var order = Order.Create(
            Buyer,
            Seller,
            [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi Prasetyo", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            Jne,
            clock,
            OrderSource.AuctionWin,
            due);

        var read = OrderItem.ToDomain(OrderItem.ToItem(order));

        read.Source.ShouldBe(OrderSource.AuctionWin);
        read.PaymentDueAt.ShouldBe(due);
        OrderItem.ToItem(read)[DynamoTable.AuctionEndSk].S.ShouldBe(DynamoTable.OrderDueSkValue(due, order.Id));
    }

    [Fact]
    public void A_row_stored_before_sources_existed_reads_as_a_sale_due_fifteen_minutes_after_creation()
    {
        var clock = new FixedClock(Start);
        var order = NewOrder(clock);
        var item = OrderItem.ToItem(order);
        item.Remove("source");
        item.Remove("paymentDueAt");

        var read = OrderItem.ToDomain(item);

        read.Source.ShouldBe(OrderSource.FixedPrice);
        read.PaymentDueAt.ShouldBe(Start + Order.PaymentWindow);
    }

    [Fact]
    public void Every_other_state_is_written_without_the_entry()
    {
        var clock = new FixedClock(Start);

        var states = new Dictionary<string, Order>
        {
            ["Paid"] = Paid(clock),
            ["Cancelled"] = Cancelled(clock),
            ["Disputed"] = Disputed(clock),
            ["Released"] = Released(clock),
        };

        foreach (var (name, order) in states)
        {
            var item = OrderItem.ToItem(order);
            item.ContainsKey(DynamoTable.AuctionEndPk).ShouldBeFalse($"{name} must not carry the partition key");
            item.ContainsKey(DynamoTable.AuctionEndSk).ShouldBeFalse($"{name} must not carry the sort key");
        }

        static Order Cancelled(FixedClock c)
        {
            var order = NewOrder(c);
            order.Cancel(c);
            return order;
        }

        static Order Disputed(FixedClock c)
        {
            var order = Delivered(c);
            order.OpenDispute("Barang tidak sesuai", Buyer, c);
            return order;
        }

        static Order Released(FixedClock c)
        {
            var order = Delivered(c);
            order.Release(confirmedByBuyer: true, c);
            return order;
        }
    }

    [Fact]
    public void An_order_moves_out_of_the_index_when_it_is_paid()
    {
        // The same order, written twice: the second write carries no entry, and because the
        // item is replaced whole that is what removes the first one.
        var clock = new FixedClock(Start);
        var order = NewOrder(clock);

        OrderItem.ToItem(order).ContainsKey(DynamoTable.AuctionEndPk).ShouldBeTrue();

        order.ConfirmPayment(order.GrandTotal, clock);

        OrderItem.ToItem(order).ContainsKey(DynamoTable.AuctionEndPk).ShouldBeFalse();
    }

    [Fact]
    public void Order_partitions_can_never_be_an_auction_partition()
    {
        // One index serves both; they are told apart by an exact partition match, so the prefixes
        // must differ for every shard and state.
        for (var shard = 0; shard < DynamoTable.AuctionShardCount; shard++)
        {
            var auction = DynamoTable.AuctionEndPkValue("Live", shard);

            foreach (var state in new[] { "AwaitingPayment", "Delivered" })
            {
                var order = DynamoTable.OrderDuePkValue(state, shard);
                order.ShouldNotBe(auction);
                order.ShouldNotStartWith("AUCTION#");
                auction.ShouldNotStartWith("ORDERDUE#");
            }
        }
    }

    [Fact]
    public void The_two_order_states_use_separate_partitions()
    {
        DynamoTable.OrderDuePkValue("AwaitingPayment", 2).ShouldBe("ORDERDUE#AwaitingPayment#2");
        DynamoTable.OrderDuePkValue("Delivered", 2).ShouldBe("ORDERDUE#Delivered#2");
    }

    [Fact]
    public void An_order_always_lands_in_the_same_shard()
    {
        // Pinned through ShardOf, whose output is part of stored data (see AuctionShardingTests).
        var order = NewOrder(new FixedClock(Start));

        var first = OrderItem.ToItem(order)[DynamoTable.AuctionEndPk].S;
        var second = OrderItem.ToItem(order)[DynamoTable.AuctionEndPk].S;

        first.ShouldBe(second);
        first.ShouldEndWith("#" + DynamoTable.ShardOf(order.Id));
    }

    [Fact]
    public void Earlier_due_times_sort_first_and_the_upper_bound_covers_the_whole_tick()
    {
        var early = DynamoTable.OrderDueSkValue(Start, "B");
        var late = DynamoTable.OrderDueSkValue(Start.AddMinutes(1), "A");
        string.CompareOrdinal(early, late).ShouldBeLessThan(0);

        // An order due at exactly the sweep's instant is found: the bound sorts above any id.
        string.CompareOrdinal(
            DynamoTable.OrderDueSkValue(Start, "ZZZZZZZZZZZZZZZZZZZZZZZZZZ"),
            DynamoTable.EndsAtUpperBound(Start)).ShouldBeLessThan(0);

        // ...and one due a moment later is not.
        string.CompareOrdinal(
            DynamoTable.OrderDueSkValue(Start.AddTicks(1), "0"),
            DynamoTable.EndsAtUpperBound(Start)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void An_order_still_reads_back_whole_with_the_entry_attached()
    {
        var order = Delivered(new FixedClock(Start));

        var read = OrderItem.ToDomain(OrderItem.ToItem(order));

        read.EscrowState.ShouldBe(EscrowState.Delivered);
        read.AutoReleaseAt.ShouldBe(order.AutoReleaseAt);
        read.Version.ShouldBe(order.Version);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
