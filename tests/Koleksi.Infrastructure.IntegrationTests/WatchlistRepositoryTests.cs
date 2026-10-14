using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary><c>DynamoWatchlistRepository</c> against a real table.</summary>
/// <remarks>
/// What only a real table can answer: that the row and the listing's counter really commit as
/// one transaction, that a refused condition leaves the counter alone, and that simultaneous
/// savers all count.
/// </remarks>
public sealed class WatchlistRepositoryTests : DynamoTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private DynamoWatchlistRepository Watchlist => new DynamoWatchlistRepository(
        Table.Client,
        new DynamoOptions { TableName = Table.TableName, ServiceUrl = DynamoLocal.ServiceUrl });

    private async Task<Listing> SeedActiveAsync()
    {
        var listing = Fixtures.ActiveListing();
        await Table.Listings.AddAsync(listing, Ct);
        return listing;
    }

    private async Task<long> WatchersOfAsync(string listingId) =>
        (await Table.Listings.FindAsync(listingId, Ct))!.WatcherCount;

    [DynamoLocalFact]
    public async Task Saving_writes_the_row_and_counts_one_watcher()
    {
        var listing = await SeedActiveAsync();

        (await Watchlist.AddAsync(Fixtures.BidderId, listing.Id, Now, Ct)).ShouldBe(WatchChange.Changed);

        (await WatchersOfAsync(listing.Id)).ShouldBe(1);
        var saved = await Watchlist.ListAsync(Fixtures.BidderId, Ct);
        saved.Select(entry => entry.ListingId).ShouldBe([listing.Id]);
        saved[0].SavedAt.ShouldBe(Now);
    }

    [DynamoLocalFact]
    public async Task Saving_twice_counts_once()
    {
        var listing = await SeedActiveAsync();

        await Watchlist.AddAsync(Fixtures.BidderId, listing.Id, Now, Ct);
        (await Watchlist.AddAsync(Fixtures.BidderId, listing.Id, Now.AddMinutes(5), Ct))
            .ShouldBe(WatchChange.Unchanged);

        (await WatchersOfAsync(listing.Id)).ShouldBe(1);

        // The first save time is kept, not overwritten by the repeat.
        (await Watchlist.ListAsync(Fixtures.BidderId, Ct)).Single().SavedAt.ShouldBe(Now);
    }

    [DynamoLocalFact]
    public async Task Removing_deletes_the_row_and_counts_one_down_and_twice_changes_nothing()
    {
        var listing = await SeedActiveAsync();
        await Watchlist.AddAsync(Fixtures.BidderId, listing.Id, Now, Ct);
        await Watchlist.AddAsync(Fixtures.RivalId, listing.Id, Now, Ct);

        (await Watchlist.RemoveAsync(Fixtures.BidderId, listing.Id, Ct)).ShouldBe(WatchChange.Changed);
        (await Watchlist.RemoveAsync(Fixtures.BidderId, listing.Id, Ct)).ShouldBe(WatchChange.Unchanged);

        (await WatchersOfAsync(listing.Id)).ShouldBe(1);
        (await Watchlist.ListAsync(Fixtures.BidderId, Ct)).ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task Removing_something_never_saved_does_not_count_down()
    {
        var listing = await SeedActiveAsync();
        await Watchlist.AddAsync(Fixtures.RivalId, listing.Id, Now, Ct);

        (await Watchlist.RemoveAsync(Fixtures.BidderId, listing.Id, Ct)).ShouldBe(WatchChange.Unchanged);

        (await WatchersOfAsync(listing.Id)).ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task A_listing_that_is_not_active_cannot_be_newly_saved_and_is_not_counted()
    {
        var draft = Fixtures.DraftListing();
        await Table.Listings.AddAsync(draft, Ct);

        (await Watchlist.AddAsync(Fixtures.BidderId, draft.Id, Now, Ct)).ShouldBe(WatchChange.ListingUnavailable);

        (await Watchlist.ListAsync(Fixtures.BidderId, Ct)).ShouldBeEmpty();
        (await WatchersOfAsync(draft.Id)).ShouldBe(0);
    }

    [DynamoLocalFact]
    public async Task Saving_a_listing_that_does_not_exist_writes_nothing()
    {
        const string ghost = "01JQ8ZC4K7N2M5V9XR3P6TW0YB";

        (await Watchlist.AddAsync(Fixtures.BidderId, ghost, Now, Ct)).ShouldBe(WatchChange.ListingUnavailable);

        (await Watchlist.ListAsync(Fixtures.BidderId, Ct)).ShouldBeEmpty();

        // And it did not conjure a partial listing row to hold a count.
        (await Table.Listings.FindAsync(ghost, Ct)).ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_listing_that_sold_since_it_was_saved_can_still_be_removed_and_counts_down()
    {
        var listing = await SeedActiveAsync();
        await Watchlist.AddAsync(Fixtures.BidderId, listing.Id, Now, Ct);

        var stored = (await Table.Listings.FindAsync(listing.Id, Ct))!;
        var version = stored.Version;
        stored.MarkSold(Fixtures.Clock);
        await Table.Listings.UpdateAsync(stored, version, Ct);

        // The seller's edit did not reset the count, and removing still works on a sold item.
        (await WatchersOfAsync(listing.Id)).ShouldBe(1);
        (await Watchlist.RemoveAsync(Fixtures.BidderId, listing.Id, Ct)).ShouldBe(WatchChange.Changed);
        (await WatchersOfAsync(listing.Id)).ShouldBe(0);
    }

    [DynamoLocalFact]
    public async Task A_save_row_whose_listing_is_gone_can_still_be_removed()
    {
        var listing = await SeedActiveAsync();
        await Watchlist.AddAsync(Fixtures.BidderId, listing.Id, Now, Ct);

        await Table.Client.DeleteItemAsync(
            Table.TableName,
            new Dictionary<string, AttributeValue>
            {
                [DynamoTable.Pk] = new() { S = DynamoTable.ListingPk(listing.Id) },
                [DynamoTable.Sk] = new() { S = DynamoTable.ListingMetaSk() },
            },
            Ct);

        (await Watchlist.RemoveAsync(Fixtures.BidderId, listing.Id, Ct)).ShouldBe(WatchChange.Changed);
        (await Watchlist.ListAsync(Fixtures.BidderId, Ct)).ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task The_count_never_goes_below_zero_even_if_it_has_drifted()
    {
        var listing = await SeedActiveAsync();
        await Watchlist.AddAsync(Fixtures.BidderId, listing.Id, Now, Ct);

        // Force the drift: the counter reads zero while a saved row still exists.
        await Table.Client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = Table.TableName,
            Key = new Dictionary<string, AttributeValue>
            {
                [DynamoTable.Pk] = new() { S = DynamoTable.ListingPk(listing.Id) },
                [DynamoTable.Sk] = new() { S = DynamoTable.ListingMetaSk() },
            },
            UpdateExpression = "SET watcherCount = :zero",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":zero"] = new() { N = "0" } },
        }, Ct);

        (await Watchlist.RemoveAsync(Fixtures.BidderId, listing.Id, Ct)).ShouldBe(WatchChange.Changed);

        (await WatchersOfAsync(listing.Id)).ShouldBe(0);
        (await Watchlist.ListAsync(Fixtures.BidderId, Ct)).ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task Many_people_saving_the_same_listing_at_once_all_count_exactly_once()
    {
        var listing = await SeedActiveAsync();
        const int people = 20;

        // Two attempts per person, concurrently: the double tap and the race in one go.
        var results = await Task.WhenAll(Enumerable.Range(0, people)
            .SelectMany(index => new[] { $"user-{index}", $"user-{index}" })
            .Select(user => Watchlist.AddAsync(user, listing.Id, Now, Ct)));

        results.Count(result => result == WatchChange.Changed).ShouldBe(people);
        results.Count(result => result == WatchChange.Unchanged).ShouldBe(people);
        (await WatchersOfAsync(listing.Id)).ShouldBe(people);
    }

    [DynamoLocalFact]
    public async Task Saves_and_unsaves_racing_each_other_leave_the_count_equal_to_the_rows()
    {
        var listing = await SeedActiveAsync();
        const int people = 12;

        foreach (var index in Enumerable.Range(0, people))
        {
            await Watchlist.AddAsync($"user-{index}", listing.Id, Now, Ct);
        }

        // Half remove while another half, new people, save.
        var removals = Enumerable.Range(0, people / 2)
            .Select(index => Watchlist.RemoveAsync($"user-{index}", listing.Id, Ct));
        var additions = Enumerable.Range(100, people / 2)
            .Select(index => Watchlist.AddAsync($"user-{index}", listing.Id, Now, Ct));

        await Task.WhenAll(removals.Concat(additions));

        (await WatchersOfAsync(listing.Id)).ShouldBe(people);
    }

    [DynamoLocalFact]
    public async Task The_list_is_newest_saved_first()
    {
        var first = await SeedActiveAsync();
        var second = Fixtures.ActiveListing("Second card");
        await Table.Listings.AddAsync(second, Ct);

        await Watchlist.AddAsync(Fixtures.BidderId, second.Id, Now, Ct);
        await Watchlist.AddAsync(Fixtures.BidderId, first.Id, Now.AddMinutes(1), Ct);

        (await Watchlist.ListAsync(Fixtures.BidderId, Ct)).Select(entry => entry.ListingId)
            .ShouldBe([first.Id, second.Id]);
    }

    [DynamoLocalFact]
    public async Task One_persons_list_never_includes_anothers()
    {
        var listing = await SeedActiveAsync();
        await Watchlist.AddAsync(Fixtures.BidderId, listing.Id, Now, Ct);

        (await Watchlist.ListAsync(Fixtures.RivalId, Ct)).ShouldBeEmpty();
    }
}
