using Koleksi.Application.Abstractions;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary><c>DynamoShippingClaimStore</c> against a real table: a proof photo or waybill backs one order only.</summary>
public sealed class ShippingClaimStoreTests : DynamoTestBase
{
    private static string[] Photos(string tag) => [$"shipping/seller/{tag}-packed", $"shipping/seller/{tag}-label"];

    [DynamoLocalFact]
    public async Task Fresh_photos_and_a_fresh_waybill_are_claimed()
    {
        var clash = await Table.ShippingClaims.TryClaimAsync("order-1", "jne", "JNE0000001", Photos("a"), Ct);

        clash.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task The_same_order_can_claim_again_so_a_retry_passes()
    {
        await Table.ShippingClaims.TryClaimAsync("order-1", "jne", "JNE0000002", Photos("b"), Ct);

        var again = await Table.ShippingClaims.TryClaimAsync("order-1", "jne", "JNE0000002", Photos("b"), Ct);

        again.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_second_order_cannot_reuse_a_proof_photo()
    {
        await Table.ShippingClaims.TryClaimAsync("order-1", "jne", "JNE0000003", Photos("c"), Ct);

        // A new waybill, but the label photo of the first order.
        var clash = await Table.ShippingClaims.TryClaimAsync(
            "order-2", "jne", "JNE0000004", ["shipping/seller/fresh-packed", Photos("c")[1]], Ct);

        clash.ShouldBe(ShippingClaimConflict.ProofReused);
    }

    [DynamoLocalFact]
    public async Task A_second_order_cannot_reuse_a_waybill_for_the_same_courier()
    {
        await Table.ShippingClaims.TryClaimAsync("order-1", "jne", "JNE0000005", Photos("d"), Ct);

        var clash = await Table.ShippingClaims.TryClaimAsync("order-2", "jne", "JNE0000005", Photos("e"), Ct);

        clash.ShouldBe(ShippingClaimConflict.TrackingReused);
    }

    [DynamoLocalFact]
    public async Task The_same_waybill_number_for_another_courier_is_a_different_waybill()
    {
        await Table.ShippingClaims.TryClaimAsync("order-1", "jne", "SHARED00001", Photos("f"), Ct);

        var other = await Table.ShippingClaims.TryClaimAsync("order-2", "sicepat", "SHARED00001", Photos("g"), Ct);

        other.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_refused_claim_leaves_nothing_behind()
    {
        await Table.ShippingClaims.TryClaimAsync("order-1", "jne", "JNE0000006", Photos("h"), Ct);

        // Order 2 brings a new waybill and new packed photo but order 1's label: refused as a whole.
        var refused = await Table.ShippingClaims.TryClaimAsync(
            "order-2", "jne", "JNE0000007", ["shipping/seller/new-packed", Photos("h")[1]], Ct);
        refused.ShouldBe(ShippingClaimConflict.ProofReused);

        // The waybill and the new photo it would have claimed are still free for a third order.
        var third = await Table.ShippingClaims.TryClaimAsync(
            "order-3", "jne", "JNE0000007", ["shipping/seller/new-packed", "shipping/seller/new-label"], Ct);
        third.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task One_photo_in_both_slots_is_claimed_once_without_clashing_with_itself()
    {
        var clash = await Table.ShippingClaims.TryClaimAsync(
            "order-1", "jne", "JNE0000008", ["shipping/seller/same", "shipping/seller/same"], Ct);

        clash.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task Two_orders_racing_for_the_same_photo_give_exactly_one_winner()
    {
        var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                return await Table.ShippingClaims.TryClaimAsync(
                    $"racer-{i}", "jne", $"RACE{i:D8}", ["shipping/seller/contested", $"shipping/seller/own-{i}"], Ct);
            }
            catch (ConcurrencyConflictException)
            {
                // Lost the write race on the row itself: also not a win.
                return ShippingClaimConflict.ProofReused;
            }
        }));

        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(outcome => outcome is null).ShouldBe(1);
    }
}
