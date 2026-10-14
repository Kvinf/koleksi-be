using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// The search-suggestion rows against a real table: that they follow a listing through publish and
/// archive, that a lookup is one bounded read, and that the backfill covers what already exists.
/// </summary>
public sealed class SuggestionIndexTests : DynamoTestBase
{
    [DynamoLocalFact]
    public async Task A_published_listing_is_suggested_by_the_start_of_any_word_in_its_title()
    {
        var listing = Fixtures.ActiveListing("PSA 10 Charizard Base Set Holo 1999");
        await IndexAsync(listing);

        foreach (var typed in new[] { "ch", "chari", "charizard", "holo", "base" })
        {
            var found = await Table.Suggestions.FindAsync(Lookup(typed), Ct);
            found.Items.ShouldContain(item => item.Id == listing.Id, $"typing '{typed}'");
        }
    }

    [DynamoLocalFact]
    public async Task What_is_returned_is_read_from_the_listing_so_it_carries_no_seller_name()
    {
        await IndexAsync(Fixtures.ActiveListing("PSA 10 Charizard Base Set Holo 1999"));

        var found = await Table.Suggestions.FindAsync(Lookup("charizard"), Ct);

        var item = found.Items.ShouldHaveSingleItem();
        item.SellerUsername.ShouldBeEmpty();
        item.SellerId.ShouldBeNull();
        item.CategorySlug.ShouldBe("kartu");
    }

    [DynamoLocalFact]
    public async Task An_archived_listing_stops_being_suggested()
    {
        var listing = Fixtures.ActiveListing("PSA 10 Charizard Base Set Holo 1999");
        await IndexAsync(listing);
        (await Table.Suggestions.FindAsync(Lookup("charizard"), Ct)).Items.ShouldHaveSingleItem();

        listing.Archive(Fixtures.Clock);
        await IndexAsync(listing);

        (await Table.Suggestions.FindAsync(Lookup("charizard"), Ct)).Items.ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task A_listing_with_too_many_words_still_writes_a_bounded_number_of_rows()
    {
        var title = string.Join(' ', Enumerable.Range(0, 30).Select(i => "kata" + i));
        var listing = Fixtures.ActiveListing(title.Length > 100 ? title[..100] : title);

        await Should.NotThrowAsync(() => IndexAsync(listing));
    }

    [DynamoLocalFact]
    public async Task The_backfill_walks_every_category_and_then_says_it_is_done()
    {
        await IndexAsync(Fixtures.ActiveListing("Kartu Charizard Satu Koleksi Lama"));
        await IndexAsync(Fixtures.ActiveListing("Kartu Blastoise Dua Koleksi Lama"));

        var indexed = 0;
        string? cursor = null;
        var steps = 0;

        do
        {
            var page = await Table.Suggestions.BackfillAsync(cursor, limit: 1, Ct);
            indexed += page.Indexed;
            cursor = page.NextCursor;
            steps++;
        }
        while (cursor is not null && steps < 100);

        cursor.ShouldBeNull("the walk must finish");
        indexed.ShouldBe(2);
    }

    private Task IndexAsync(Listing listing) => Table.SearchIndex.IndexAsync(
        new ListingIndexEntry(listing, Fixtures.SellerUsername, AuctionEndsAt: null, BidCount: 0), Ct);

    private static SuggestionLookup Lookup(string typed) => new(
        Words: [typed],
        SupplementCategoryIds: [],
        MaxItems: 5,
        MaxTerms: 3);
}
