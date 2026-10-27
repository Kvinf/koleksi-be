using System.Buffers.Text;
using System.Text;
using Koleksi.Application.Abstractions;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// A page cursor is opaque but not a secret: a caller can decode it and hand back one they made up. DynamoDB refuses
/// a start key that does not fit the query, which keeps other people's partitions out of reach; that refusal must reach
/// the caller as a bad request, not as an unhandled error (QA P-01).
/// </summary>
public sealed class ForgedCursorTests : DynamoTestBase
{
    private const char Pair = (char)0x1F;
    private const char Field = (char)0x1E;

    /// <summary>Cursors that decode cleanly but name keys that belong to no page of any list.</summary>
    public static TheoryData<string> Forged => new()
    {
        Cursor(("pk", "USER#x"), ("sk", "y")),
        Cursor(("pk", "ORDER#whatever"), ("sk", "META"), ("gsi_buyer_pk", "BUYER#someone"), ("gsi_buyer_sk", "ORDER#zzz")),
        Cursor(("pk", "ORDER#a"), ("sk", "META"), ("gsi_seller_pk", "SELLER#someone-else"), ("gsi_seller_sk", "ORDER#zzz")),
        Cursor(("pk", string.Empty)),
    };

    private static string Cursor(params (string Name, string Value)[] key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            string.Join(Pair, key.Select(part => $"S{Field}{part.Name}{Field}{part.Value}"))));

    [DynamoLocalTheory]
    [MemberData(nameof(Forged))]
    public async Task A_forged_cursor_on_the_order_lists_is_an_invalid_cursor(string cursor)
    {
        await Should.ThrowAsync<InvalidCursorException>(() => Table.Orders.ListForBuyerAsync("buyer-1", cursor, 10, Ct));
        await Should.ThrowAsync<InvalidCursorException>(() => Table.Orders.ListForSellerAsync("seller-1", cursor, 10, Ct));
    }

    [DynamoLocalTheory]
    [MemberData(nameof(Forged))]
    public async Task A_forged_cursor_on_the_offer_lists_is_an_invalid_cursor(string cursor)
    {
        await Should.ThrowAsync<InvalidCursorException>(() => Table.Offers.ListForBuyerAsync("buyer-1", cursor, 10, Ct));
        await Should.ThrowAsync<InvalidCursorException>(() => Table.Offers.ListForSellerAsync("seller-1", cursor, 10, Ct));
    }

    [DynamoLocalTheory]
    [MemberData(nameof(Forged))]
    public async Task A_forged_cursor_on_the_listing_review_and_payout_lists_is_an_invalid_cursor(string cursor)
    {
        await Should.ThrowAsync<InvalidCursorException>(() => Table.Listings.ListBySellerAsync("seller-1", null, cursor, 10, Ct));
        await Should.ThrowAsync<InvalidCursorException>(() => Table.Reviews.ListBySellerAsync("seller-1", cursor, 10, Ct));
        await Should.ThrowAsync<InvalidCursorException>(() => Table.Payouts.ListForSellerAsync("seller-1", cursor, 10, Ct));
    }

    [DynamoLocalFact]
    public async Task A_missing_or_unreadable_cursor_still_starts_at_the_first_page()
    {
        (await Table.Orders.ListForBuyerAsync("buyer-1", null, 10, Ct)).Items.ShouldBeEmpty();
        (await Table.Orders.ListForBuyerAsync("buyer-1", "not base64 !!", 10, Ct)).Items.ShouldBeEmpty();
    }
}
