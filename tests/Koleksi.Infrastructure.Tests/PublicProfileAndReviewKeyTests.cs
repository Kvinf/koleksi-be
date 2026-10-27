using Koleksi.Domain.Reviews;
using Koleksi.Domain.Users;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>Key composition and item mapping for public profiles, handle claims and reviews.</summary>
public sealed class PublicProfileAndReviewKeyTests
{
    private const string UserId = "b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33";
    private const string OrderId = "01JQ8ZC4K7N2M5V9XR3P6TW0YB";

    [Fact]
    public void The_public_identity_sits_beside_the_private_profile_but_is_a_separate_item()
    {
        DynamoTable.UserPk(UserId).ShouldBe("USER#" + UserId);
        DynamoTable.PublicProfileSk().ShouldBe("#PUBLIC");
        DynamoTable.PublicProfileSk().ShouldNotBe(DynamoTable.ProfileSk());
    }

    [Fact]
    public void A_handle_claim_is_keyed_by_the_handle_alone()
    {
        DynamoTable.HandlePk("budi").ShouldBe("HANDLE#budi");
        DynamoTable.HandleSk().ShouldBe("#META");
        Should.Throw<ArgumentException>(() => DynamoTable.HandlePk(" "));
    }

    [Fact]
    public void A_profile_and_its_claim_round_trip_through_their_items()
    {
        var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var profile = PublicProfile.Rehydrate(UserId, "budi", "Toko Budi", createdAt, createdAt.AddDays(1), createdAt.AddDays(2), 3);

        var item = PublicProfileItem.ToItem(profile);
        var back = PublicProfileItem.ToDomain(item);

        back.UserId.ShouldBe(UserId);
        back.Handle.ShouldBe("budi");
        back.DisplayName.ShouldBe("Toko Budi");
        back.MemberSince.ShouldBe(createdAt);
        back.HandleChangedAt.ShouldBe(createdAt.AddDays(1));
        back.Version.ShouldBe(3);

        var claim = PublicProfileItem.ToHandleItem("budi", UserId);
        claim[DynamoTable.Pk].S.ShouldBe("HANDLE#budi");
        claim[PublicProfileItem.UserIdAttribute].S.ShouldBe(UserId);
    }

    [Fact]
    public void A_profile_without_a_display_name_reads_back_without_one()
    {
        var now = DateTimeOffset.UnixEpoch;
        var back = PublicProfileItem.ToDomain(
            PublicProfileItem.ToItem(PublicProfile.Rehydrate(UserId, "budi", null, now, null, now, 0)));

        back.DisplayName.ShouldBeNull();
        back.HandleChangedAt.ShouldBeNull();
    }

    [Fact]
    public void A_review_is_one_item_per_order_and_one_per_seller_feed_row()
    {
        // The order's item is what makes a second review a failed condition; it must live at a
        // key only that order can produce.
        DynamoTable.OrderReviewPk(OrderId).ShouldBe("ORDER#" + OrderId);
        DynamoTable.OrderReviewSk().ShouldBe("#REVIEW");
        DynamoTable.OrderReviewSk().ShouldNotBe(DynamoTable.OrderMetaSk());

        DynamoTable.SellerReviewsPk(UserId).ShouldBe("REVIEWS#" + UserId);
        DynamoTable.SellerReviewSk("01JQ8ZC4K7N2M5V9XR3P6TW0YC").ShouldStartWith(DynamoTable.SellerReviewSkPrefix());
    }

    [Fact]
    public void The_counters_sort_outside_the_feed_range_and_newer_reviews_sort_later()
    {
        // The feed read is begins_with(sk, "REVIEW#"); the counters must never fall inside it.
        DynamoTable.SellerReviewStatsSk().ShouldNotStartWith(DynamoTable.SellerReviewSkPrefix());

        string.CompareOrdinal(
            DynamoTable.SellerReviewSk("01JQ8ZC4K7N2M5V9XR3P6TW0YB"),
            DynamoTable.SellerReviewSk("01JQ8ZC4K8N2M5V9XR3P6TW0YB")).ShouldBeLessThan(0);
    }

    [Fact]
    public void Neither_copy_of_a_review_carries_an_index_key_so_it_cannot_leak_into_order_lists()
    {
        var review = Review.Rehydrate(
            "01JQ8ZC4K7N2M5V9XR3P6TW0YC", OrderId, "L1", UserId, "buyer", 4, "Mantap", true, DateTimeOffset.UnixEpoch);

        foreach (var item in new[] { ReviewItem.ToOrderItem(review), ReviewItem.ToFeedItem(review) })
        {
            item.ContainsKey(DynamoTable.SellerPk).ShouldBeFalse();
            item.ContainsKey(DynamoTable.BuyerPk).ShouldBeFalse();
        }
    }

    [Fact]
    public void A_review_and_the_counters_round_trip_through_their_items()
    {
        var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var review = Review.Rehydrate("01JQ8ZC4K7N2M5V9XR3P6TW0YC", OrderId, "L1", UserId, "buyer", 4, "Mantap", true, createdAt);

        var back = ReviewItem.ToDomain(ReviewItem.ToFeedItem(review));

        back.Id.ShouldBe(review.Id);
        back.OrderId.ShouldBe(OrderId);
        back.Rating.ShouldBe(4);
        back.Text.ShouldBe("Mantap");
        back.IsAnonymous.ShouldBeTrue();
        back.CreatedAt.ShouldBe(createdAt);

        var stats = ReviewItem.ToStats(new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
        {
            [ReviewItem.CountAttribute] = Attr.N(3),
            [ReviewItem.SumAttribute] = Attr.N(13),
            [ReviewItem.StarAttribute(3)] = Attr.N(1),
            [ReviewItem.StarAttribute(4)] = Attr.N(1),
            [ReviewItem.StarAttribute(5)] = Attr.N(1),
        });

        stats.Count.ShouldBe(3);
        stats.Sum.ShouldBe(13);
        stats.Histogram.ShouldBe([0L, 0L, 1L, 1L, 1L]);
    }

    [Fact]
    public void A_review_with_no_text_reads_back_with_none()
    {
        var review = Review.Rehydrate("01JQ8ZC4K7N2M5V9XR3P6TW0YC", OrderId, "L1", UserId, "buyer", 5, null, false, DateTimeOffset.UnixEpoch);

        ReviewItem.ToDomain(ReviewItem.ToOrderItem(review)).Text.ShouldBeNull();
    }

    [Fact]
    public void The_browse_projection_carries_the_seller_id_so_the_handle_can_be_resolved_at_read_time()
    {
        BrowseProjectionItem.SellerIdAttr.ShouldBe("sellerId");
    }
}
