using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// <c>DynamoListingSearchIndex</c> against a real table.
/// </summary>
/// <remarks>
/// <para>
/// This is where DynamoDB's limits either hold or do not. Sorting is delegated to an index
/// per order, so price ordering is only correct if the zero-padded key really does sort
/// the way the padding intends; filtering happens after the read, so a facet filter is
/// only exact if the expression binds every value as a placeholder. Neither can be
/// verified against a fake.
/// </para>
/// <para>
/// The counters are the other half. They are maintained by atomic adds on every index and
/// remove, and they drive the result count and the page count — so a counter that only
/// ever grows quietly fills the sidebar with facets that match nothing.
/// </para>
/// </remarks>
public sealed class SearchIndexTests : DynamoTestBase
{
    [DynamoLocalFact]
    public async Task An_indexed_listing_is_findable_and_renders_as_a_card()
    {
        var listing = Fixtures.ActiveListing();
        await IndexAsync(listing);

        var page = await Table.SearchIndex.SearchAsync(Query(), Ct);

        var card = page.Items.ShouldHaveSingleItem();
        card.Id.ShouldBe(listing.Id);
        card.Title.ShouldBe(listing.Title);
        card.PriceRupiah.ShouldBe(listing.Price.Rupiah);
        card.SellerUsername.ShouldBe(Fixtures.SellerUsername);
        card.City.ShouldBe(listing.City);
        card.Condition.ShouldBe(listing.Condition);
        card.PrimaryPhotoKey.ShouldBe("listing/photo-one.jpg");

        // The slug comes from the category, not from the row. Browse links are built from
        // it, so a projection that carried its own copy could drift out of step.
        card.CategorySlug.ShouldBe("kartu");
    }

    [DynamoLocalFact]
    public async Task A_listing_that_is_not_publicly_visible_is_absent_rather_than_filtered()
    {
        // A row that exists but must never be returned is one forgotten filter expression
        // away from being returned. Drafts are simply not in the projection.
        var draft = Fixtures.DraftListing();
        await IndexAsync(draft);

        var page = await Table.SearchIndex.SearchAsync(Query(), Ct);

        page.Items.ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task Archiving_a_listing_takes_it_out_of_browse()
    {
        var listing = Fixtures.ActiveListing();
        await IndexAsync(listing);
        (await Table.SearchIndex.SearchAsync(Query(), Ct)).Items.ShouldHaveSingleItem();

        listing.Archive(Fixtures.Clock);
        await IndexAsync(listing);

        (await Table.SearchIndex.SearchAsync(Query(), Ct)).Items.ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task Price_ordering_is_by_value_not_by_digit_count()
    {
        // The padding test that matters. Unpadded, "9000" sorts after "10000000" and the
        // cheapest-first page opens with the most expensive lot.
        long[] prices = [9_000_000, 25_000, 1_250_000, 10_000, 300_000];

        foreach (var price in prices)
        {
            await IndexAsync(Fixtures.ActiveListing(
                $"Kartu Koleksi Dengan Harga {price} Rupiah", priceRupiah: price));
        }

        var ascending = await Table.SearchIndex.SearchAsync(
            Query() with { Sort = BrowseSort.PriceAscending, PageSize = 10 }, Ct);

        ascending.Items.Select(item => item.PriceRupiah)
            .ShouldBe([10_000, 25_000, 300_000, 1_250_000, 9_000_000]);

        var descending = await Table.SearchIndex.SearchAsync(
            Query() with { Sort = BrowseSort.PriceDescending, PageSize = 10 }, Ct);

        descending.Items.Select(item => item.PriceRupiah)
            .ShouldBe([9_000_000, 1_250_000, 300_000, 25_000, 10_000]);
    }

    [DynamoLocalFact]
    public async Task Newest_first_is_the_default_order()
    {
        var first = Fixtures.ActiveListing("Kartu Koleksi Yang Terbit Lebih Dahulu Sekali");
        var second = Fixtures.ActiveListing("Kartu Koleksi Yang Terbit Sesudahnya Kemudian");

        await IndexAsync(first);
        await IndexAsync(second);

        var page = await Table.SearchIndex.SearchAsync(Query() with { PageSize = 10 }, Ct);

        // Ids are time-ordered, so newest first is a descending key scan.
        var ids = page.Items.Select(item => item.Id).ToList();
        ids.ShouldBe(ids.OrderByDescending(id => id, StringComparer.Ordinal).ToList());
    }

    [DynamoLocalFact]
    public async Task A_facet_filter_returns_only_the_rows_that_carry_that_value()
    {
        await IndexAsync(Fixtures.ActiveListing("Kartu Charizard Base Set Grade Sempurna", grade: "PSA 10"));
        await IndexAsync(Fixtures.ActiveListing("Kartu Blastoise Base Set Grade Sangat Baik", grade: "PSA 9"));
        await IndexAsync(Fixtures.ActiveListing("Kartu Venusaur Base Set Tanpa Grading Resmi", grade: "Raw"));

        var page = await Table.SearchIndex.SearchAsync(
            Query() with
            {
                SelectedFacets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["grade"] = ["PSA 10"],
                },
            },
            Ct);

        page.Items.ShouldHaveSingleItem().Condition.ShouldBe("PSA 10");
    }

    [DynamoLocalFact]
    public async Task Values_within_one_facet_are_alternatives()
    {
        // A shopper wants PSA 9 *or* PSA 10. Separate facets are still conjunctions.
        await IndexAsync(Fixtures.ActiveListing("Kartu Charizard Base Set Grade Sempurna", grade: "PSA 10"));
        await IndexAsync(Fixtures.ActiveListing("Kartu Blastoise Base Set Grade Sangat Baik", grade: "PSA 9"));
        await IndexAsync(Fixtures.ActiveListing("Kartu Venusaur Base Set Tanpa Grading Resmi", grade: "Raw"));

        var page = await Table.SearchIndex.SearchAsync(
            Query() with
            {
                SelectedFacets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["grade"] = ["PSA 10", "PSA 9"],
                },
                PageSize = 10,
            },
            Ct);

        page.Items.Count.ShouldBe(2);
        page.Items.ShouldNotContain(item => item.Condition == "Raw");
    }

    [DynamoLocalFact]
    public async Task Two_different_facets_narrow_together()
    {
        await IndexAsync(Fixtures.ActiveListing(
            "Kartu Charizard Base Set Grade Sempurna", grade: "PSA 10", set: "Base Set"));
        await IndexAsync(Fixtures.ActiveListing(
            "Kartu Lugia Neo Genesis Grade Sempurna Juga", grade: "PSA 10", set: "Neo Genesis"));

        var page = await Table.SearchIndex.SearchAsync(
            Query() with
            {
                SelectedFacets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["grade"] = ["PSA 10"],
                    ["set"] = ["Neo Genesis"],
                },
                PageSize = 10,
            },
            Ct);

        page.Items.ShouldHaveSingleItem().Title.ShouldContain("Lugia");
    }

    [DynamoLocalFact]
    public async Task A_facet_value_carrying_expression_syntax_is_matched_literally()
    {
        // Every value is bound as a placeholder. Were one concatenated into the filter
        // string it would be injection into DynamoDB's expression language — the same
        // class of bug as string-built SQL.
        var hostile = "PSA 10\" OR #status = :active OR \"x";

        await IndexAsync(Fixtures.ActiveListing("Kartu Charizard Base Set Grade Sempurna", grade: "PSA 10"));

        var page = await Table.SearchIndex.SearchAsync(
            Query() with
            {
                SelectedFacets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["grade"] = [hostile],
                },
                PageSize = 10,
            },
            Ct);

        page.Items.ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task A_price_range_and_a_city_narrow_the_results()
    {
        await IndexAsync(Fixtures.ActiveListing(
            "Kartu Koleksi Murah Dari Penjual Bandung", priceRupiah: 150_000, city: "Bandung"));
        await IndexAsync(Fixtures.ActiveListing(
            "Kartu Koleksi Mahal Dari Penjual Bandung", priceRupiah: 8_000_000, city: "Bandung"));
        await IndexAsync(Fixtures.ActiveListing(
            "Kartu Koleksi Murah Dari Penjual Surabaya", priceRupiah: 150_000, city: "Surabaya"));

        var page = await Table.SearchIndex.SearchAsync(
            Query() with { MaxPriceRupiah = 1_000_000, City = "Bandung", PageSize = 10 }, Ct);

        var card = page.Items.ShouldHaveSingleItem();
        card.City.ShouldBe("Bandung");
        card.PriceRupiah.ShouldBe(150_000);
    }

    [DynamoLocalFact]
    public async Task Free_text_matches_a_substring_of_the_title_regardless_of_case()
    {
        await IndexAsync(Fixtures.ActiveListing("Kartu Charizard Base Set Grade Sempurna"));
        await IndexAsync(Fixtures.ActiveListing("Kartu Blastoise Base Set Grade Sangat Baik"));

        var page = await Table.SearchIndex.SearchAsync(
            Query() with { SearchTerm = "CHARIZARD", PageSize = 10 }, Ct);

        page.Items.ShouldHaveSingleItem().Title.ShouldContain("Charizard");
    }

    [DynamoLocalFact]
    public async Task Only_live_auctions_appear_in_the_ending_soon_order()
    {
        // The ending-soon index is sparse. A fixed-price listing costs nothing there, and
        // a closed auction drops out on its next write.
        var auctionListing = Fixtures.ActiveListing(
            "Kartu Lelang Yang Sedang Berjalan Sekarang", format: ListingFormat.Auction);
        var fixedListing = Fixtures.ActiveListing("Kartu Harga Pas Tanpa Proses Lelang");

        await Table.SearchIndex.IndexAsync(
            new ListingIndexEntry(
                auctionListing, Fixtures.SellerUsername, FixedClock.Origin.AddDays(2), 4),
            Ct);

        await IndexAsync(fixedListing);

        var page = await Table.SearchIndex.SearchAsync(
            Query() with { Sort = BrowseSort.AuctionsEndingSoon, PageSize = 10 }, Ct);

        var card = page.Items.ShouldHaveSingleItem();
        card.Id.ShouldBe(auctionListing.Id);
        card.AuctionEndsAt.ShouldBe(FixedClock.Origin.AddDays(2));
        card.BidCount.ShouldBe(4);
    }

    [DynamoLocalFact]
    public async Task Numbered_paging_walks_to_the_requested_page()
    {
        // DynamoDB has no offset, so page three is a walk from page one. Bounded twice —
        // by MaxPageNumber and by the round-trip ceiling — but still a walk.
        for (var i = 0; i < 7; i++)
        {
            await IndexAsync(Fixtures.ActiveListing(
                $"Kartu Koleksi Untuk Uji Halaman Bernomor {i:D2}", priceRupiah: 100_000 + (i * 1_000)));
        }

        var first = await Table.SearchIndex.SearchAsync(
            Query() with { Sort = BrowseSort.PriceAscending, PageSize = 3, PageNumber = 1 }, Ct);
        var second = await Table.SearchIndex.SearchAsync(
            Query() with { Sort = BrowseSort.PriceAscending, PageSize = 3, PageNumber = 2 }, Ct);
        var third = await Table.SearchIndex.SearchAsync(
            Query() with { Sort = BrowseSort.PriceAscending, PageSize = 3, PageNumber = 3 }, Ct);

        first.Items.Count.ShouldBe(3);
        second.Items.Count.ShouldBe(3);
        third.Items.Count.ShouldBe(1);

        var seen = first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Id).ToList();
        seen.Distinct(StringComparer.Ordinal).Count().ShouldBe(7);

        // Prices ascend across page boundaries, not only within a page.
        var prices = first.Items.Concat(second.Items).Concat(third.Items)
            .Select(item => item.PriceRupiah).ToList();
        prices.ShouldBe(prices.Order().ToList());
    }

    [DynamoLocalFact]
    public async Task Facet_counters_rise_when_a_listing_is_indexed()
    {
        await IndexAsync(Fixtures.ActiveListing("Kartu Charizard Base Set Grade Sempurna", grade: "PSA 10"));
        await IndexAsync(Fixtures.ActiveListing("Kartu Lugia Neo Genesis Grade Sempurna Juga", grade: "PSA 10"));
        await IndexAsync(Fixtures.ActiveListing("Kartu Blastoise Base Set Grade Sangat Baik", grade: "PSA 9"));

        var counts = await Table.SearchIndex.CountFacetsAsync(Query(), Ct);

        Count(counts, "grade", "PSA 10").ShouldBe(2);
        Count(counts, "grade", "PSA 9").ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task Facet_counters_fall_when_a_listing_leaves_browse()
    {
        // Counters that only ever grow leave the sidebar advertising filters that match
        // nothing, and the pager promising pages that do not exist.
        var listing = Fixtures.ActiveListing("Kartu Charizard Base Set Grade Sempurna", grade: "PSA 10");
        await IndexAsync(listing);

        Count(await Table.SearchIndex.CountFacetsAsync(Query(), Ct), "grade", "PSA 10").ShouldBe(1);

        await Table.SearchIndex.RemoveAsync(listing.Id, Ct);

        Count(await Table.SearchIndex.CountFacetsAsync(Query(), Ct), "grade", "PSA 10").ShouldBe(0);
    }

    [DynamoLocalFact]
    public async Task Removing_a_listing_twice_does_not_drive_a_counter_negative()
    {
        // Remove reads the deleted row to know what to decrement. A second remove finds
        // nothing and must therefore decrement nothing.
        var listing = Fixtures.ActiveListing("Kartu Charizard Base Set Grade Sempurna", grade: "PSA 10");
        await IndexAsync(listing);

        await Table.SearchIndex.RemoveAsync(listing.Id, Ct);
        await Table.SearchIndex.RemoveAsync(listing.Id, Ct);

        Count(await Table.SearchIndex.CountFacetsAsync(Query(), Ct), "grade", "PSA 10").ShouldBe(0);
    }

    [DynamoLocalFact]
    public async Task The_result_total_is_never_below_what_was_actually_returned()
    {
        // The counters can lag a write. "Showing 1–3 of 0" is a bug the reader can see, so
        // the total is floored at the number of rows in hand.
        await IndexAsync(Fixtures.ActiveListing("Kartu Charizard Base Set Grade Sempurna"));
        await IndexAsync(Fixtures.ActiveListing("Kartu Blastoise Base Set Grade Sangat Baik"));

        var page = await Table.SearchIndex.SearchAsync(Query() with { PageSize = 10 }, Ct);

        page.TotalCount.ShouldBeGreaterThanOrEqualTo(page.Items.Count);
        page.TotalCount.ShouldBe(2);
    }

    [DynamoLocalFact]
    public async Task A_search_with_no_category_fans_out_across_all_of_them()
    {
        await IndexAsync(Fixtures.ActiveListing("Kartu Charizard Base Set Grade Sempurna"));

        var page = await Table.SearchIndex.SearchAsync(
            Query() with { CategoryId = null, PageSize = 10 }, Ct);

        page.Items.ShouldHaveSingleItem().CategorySlug.ShouldBe("kartu");
    }

    [DynamoLocalFact]
    public async Task Updating_the_price_changes_the_card_without_a_full_reindex()
    {
        var listing = Fixtures.ActiveListing(priceRupiah: 1_000_000);
        await IndexAsync(listing);

        await Table.SearchIndex.UpdatePriceAsync(listing.Id, currentPriceRupiah: 3_500_000, bidCount: 2, Ct);

        var card = (await Table.SearchIndex.SearchAsync(Query(), Ct)).Items.ShouldHaveSingleItem();
        card.PriceRupiah.ShouldBe(3_500_000);
        card.BidCount.ShouldBe(2);

        // Everything else the full write set — title, seller, photo — is untouched.
        card.Title.ShouldBe(listing.Title);
        card.SellerUsername.ShouldBe(Fixtures.SellerUsername);
    }

    [DynamoLocalFact]
    public async Task Updating_the_price_of_a_listing_that_is_not_indexed_is_a_quiet_no_op()
    {
        // A bid can theoretically land before the listing's own publish write has settled.
        // There is nothing to catch up yet, and that is not this call's problem to solve.
        await Should.NotThrowAsync(() =>
            Table.SearchIndex.UpdatePriceAsync(EntityId.New(), currentPriceRupiah: 1, bidCount: 1, Ct));
    }

    private Task IndexAsync(Listing listing) => Table.SearchIndex.IndexAsync(
        new ListingIndexEntry(listing, Fixtures.SellerUsername, AuctionEndsAt: null, BidCount: 0), Ct);

    private static BrowseQuery Query() => new(
        CategoryId: Fixtures.CardsCategory,
        SearchTerm: null,
        SelectedFacets: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        MinPriceRupiah: null,
        MaxPriceRupiah: null,
        City: null,
        Sort: BrowseSort.Newest,
        PageNumber: 1,
        PageSize: BrowseQuery.DefaultPageSize);

    private static long Count(IReadOnlyList<FacetCount> counts, string facetKey, string value) =>
        counts.FirstOrDefault(count =>
            string.Equals(count.FacetKey, facetKey, StringComparison.Ordinal)
            && string.Equals(count.Value, value, StringComparison.Ordinal))?.Count ?? 0;
}
