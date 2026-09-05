using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// <c>DynamoListingRepository</c> against a real table.
/// </summary>
/// <remarks>
/// The two things only a real table can answer: whether every field survives the round
/// trip through DynamoDB's type system, and whether the conditional writes actually
/// condition on what they claim to.
/// </remarks>
public sealed class ListingRepositoryTests : DynamoTestBase
{
    [DynamoLocalFact]
    public async Task A_listing_survives_the_round_trip_field_for_field()
    {
        var listing = Fixtures.ActiveListing();
        listing.SetAutoDeclineThreshold(70m, Fixtures.Clock);

        await Table.Listings.AddAsync(listing, Ct);

        var stored = await Table.Listings.FindAsync(listing.Id, Ct);

        stored.ShouldNotBeNull();
        stored.Id.ShouldBe(listing.Id);
        stored.SellerId.ShouldBe(listing.SellerId);
        stored.CategoryId.ShouldBe(listing.CategoryId);
        stored.Title.ShouldBe(listing.Title);
        stored.Description.ShouldBe(listing.Description);
        stored.Condition.ShouldBe(listing.Condition);
        stored.City.ShouldBe(listing.City);
        stored.Status.ShouldBe(listing.Status);
        stored.Format.ShouldBe(listing.Format);
        stored.Version.ShouldBe(listing.Version);

        // Money is a long of whole rupiah. A float somewhere in the mapping would show up
        // here as a price that is a rupiah out.
        stored.Price.Rupiah.ShouldBe(listing.Price.Rupiah);

        // Decimal, not double: 70 percent has to come back as exactly 70.
        stored.AutoDeclineBelowPercent.ShouldBe(70m);

        stored.Photos.Count.ShouldBe(1);
        stored.Photos[0].StorageKey.ShouldBe("listing/photo-one.jpg");
        stored.Photos[0].Width.ShouldBe(1200);
        stored.Photos[0].Height.ShouldBe(1200);

        stored.Attributes["grade"].ShouldBe("PSA 10");
        stored.Attributes["set"].ShouldBe("Base Set");

        // Instants are stored as round-trip UTC strings so they sort; they must come back
        // as the same instant, not merely the same wall clock.
        stored.CreatedAt.ShouldBe(listing.CreatedAt);
        stored.UpdatedAt.ShouldBe(listing.UpdatedAt);
        stored.PublishedAt.ShouldBe(listing.PublishedAt);
    }

    [DynamoLocalFact]
    public async Task A_listing_that_does_not_exist_reads_back_as_null()
    {
        var missing = await Table.Listings.FindAsync(
            EntityId.New(), Ct);

        missing.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task Adding_the_same_listing_twice_is_refused()
    {
        // Ids are UUIDv7 so a genuine collision is a bug, but an unconditional put would
        // also let a replayed create reset a live listing back to its draft state.
        var listing = Fixtures.ActiveListing();
        await Table.Listings.AddAsync(listing, Ct);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Table.Listings.AddAsync(listing, Ct));
    }

    [DynamoLocalFact]
    public async Task An_update_at_the_version_that_was_read_succeeds()
    {
        var listing = Fixtures.ActiveListing();
        await Table.Listings.AddAsync(listing, Ct);

        var expectedVersion = listing.Version;
        listing.ChangePrice(Money.FromRupiah(3_100_000), Fixtures.Clock);

        await Table.Listings.UpdateAsync(listing, expectedVersion, Ct);

        var stored = await Table.Listings.FindAsync(listing.Id, Ct);
        stored.ShouldNotBeNull();
        stored.Price.Rupiah.ShouldBe(3_100_000);
        stored.Version.ShouldBe(expectedVersion + 1);
    }

    [DynamoLocalFact]
    public async Task An_update_at_a_stale_version_is_refused()
    {
        // The seller with two tabs open. Without this condition, one of the two edits is
        // silently discarded and neither tab is told.
        var listing = Fixtures.ActiveListing();
        await Table.Listings.AddAsync(listing, Ct);

        var staleVersion = listing.Version;
        listing.ChangePrice(Money.FromRupiah(3_100_000), Fixtures.Clock);
        await Table.Listings.UpdateAsync(listing, staleVersion, Ct);

        listing.ChangePrice(Money.FromRupiah(4_000_000), Fixtures.Clock);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Table.Listings.UpdateAsync(listing, staleVersion, Ct));

        var stored = await Table.Listings.FindAsync(listing.Id, Ct);
        stored.ShouldNotBeNull();
        stored.Price.Rupiah.ShouldBe(3_100_000);
    }

    [DynamoLocalFact]
    public async Task An_update_of_a_listing_that_was_never_written_is_refused()
    {
        // Version zero takes the "attribute_not_exists OR equals" branch, which is the one
        // that could accidentally create a row that no AddAsync ever validated.
        var listing = Fixtures.ActiveListing();

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Table.Listings.UpdateAsync(listing, 0, Ct));

        var stored = await Table.Listings.FindAsync(listing.Id, Ct);
        stored.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_sellers_dashboard_tab_reads_only_that_status()
    {
        var active = Fixtures.ActiveListing("Kartu Pokemon Charizard Holo Grade Tinggi");
        var draft = Fixtures.DraftListing();
        var archived = Fixtures.ActiveListing("Kartu One Piece Luffy Manga Rare Print");
        archived.Archive(Fixtures.Clock);

        foreach (var listing in new[] { active, draft, archived })
        {
            await Table.Listings.AddAsync(listing, Ct);
        }

        var page = await Table.Listings.ListBySellerAsync(
            Fixtures.SellerId, ListingStatus.Active, cursor: null, limit: 20,
            Ct);

        // Status leads the sort key, so this is a key condition rather than a filter. If it
        // silently became a filter it would still be correct here — and would bill for
        // every listing the seller has ever created.
        page.Items.Count.ShouldBe(1);
        page.Items[0].Id.ShouldBe(active.Id);
    }

    [DynamoLocalFact]
    public async Task A_seller_with_no_status_filter_sees_everything_they_own_newest_first()
    {
        var older = Fixtures.ActiveListing("Kartu Yu Gi Oh Blue Eyes White Dragon LOB");
        var newer = Fixtures.ActiveListing("Kartu Magic The Gathering Black Lotus Proxy");

        // Ids are time-ordered, so the later id is the newer listing regardless of when
        // the rows were written.
        var (first, second) = string.CompareOrdinal(older.Id, newer.Id) < 0
            ? (older, newer)
            : (newer, older);

        await Table.Listings.AddAsync(first, Ct);
        await Table.Listings.AddAsync(second, Ct);

        var page = await Table.Listings.ListBySellerAsync(
            Fixtures.SellerId, status: null, cursor: null, limit: 20,
            Ct);

        page.Items.Count.ShouldBeGreaterThanOrEqualTo(2);
        page.Items[0].Id.ShouldBe(second.Id);
    }

    [DynamoLocalFact]
    public async Task A_cursor_from_one_page_starts_the_next_one_where_it_left_off()
    {
        // The cursor is the one value in the system that is encoded here and decoded from
        // a URL. A round trip that loses a byte silently repeats or skips a page.
        for (var i = 0; i < 5; i++)
        {
            await Table.Listings.AddAsync(
                Fixtures.ActiveListing($"Kartu Koleksi Nomor Seri Terbatas Edisi {i}"),
                Ct);
        }

        var first = await Table.Listings.ListBySellerAsync(
            Fixtures.SellerId, ListingStatus.Active, cursor: null, limit: 2,
            Ct);

        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNullOrWhiteSpace();

        var second = await Table.Listings.ListBySellerAsync(
            Fixtures.SellerId, ListingStatus.Active, first.NextCursor, limit: 2,
            Ct);

        second.Items.Count.ShouldBe(2);
        second.Items.Select(item => item.Id)
            .ShouldNotContain(first.Items[0].Id);
        second.Items.Select(item => item.Id)
            .ShouldNotContain(first.Items[1].Id);
    }

    [DynamoLocalFact]
    public async Task A_view_is_counted_without_touching_the_concurrency_token()
    {
        // Views are unconditional atomic adds, deliberately outside the version path: a
        // page view must never fail a seller's concurrent edit.
        var listing = Fixtures.ActiveListing();
        await Table.Listings.AddAsync(listing, Ct);

        await Table.Listings.IncrementViewCountAsync(listing.Id, Ct);
        await Table.Listings.IncrementViewCountAsync(listing.Id, Ct);
        await Table.Listings.IncrementViewCountAsync(listing.Id, Ct);

        var stored = await Table.Listings.FindAsync(listing.Id, Ct);
        stored.ShouldNotBeNull();
        stored.ViewCount.ShouldBe(3);
        stored.Version.ShouldBe(listing.Version);
    }

    [DynamoLocalFact]
    public async Task Concurrent_views_are_all_counted()
    {
        var listing = Fixtures.ActiveListing();
        await Table.Listings.AddAsync(listing, Ct);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Table.Listings.IncrementViewCountAsync(listing.Id, Ct)));

        var stored = await Table.Listings.FindAsync(listing.Id, Ct);
        stored.ShouldNotBeNull();

        // An ADD, not a read-modify-write. The latter would lose most of these.
        stored.ViewCount.ShouldBe(20);
    }

    [DynamoLocalFact]
    public async Task An_edit_does_not_roll_back_views_counted_since_it_was_read()
    {
        // The reason the view counter is absent from ListingItem.ToItem. Writing it back
        // from a stale aggregate would undo every view since the seller opened the form.
        var listing = Fixtures.ActiveListing();
        await Table.Listings.AddAsync(listing, Ct);

        var expectedVersion = listing.Version;
        await Table.Listings.IncrementViewCountAsync(listing.Id, Ct);

        listing.SetDescription("Deskripsi diperbarui oleh penjual.", Fixtures.Clock);
        await Table.Listings.UpdateAsync(listing, expectedVersion, Ct);

        var stored = await Table.Listings.FindAsync(listing.Id, Ct);
        stored.ShouldNotBeNull();
        stored.ViewCount.ShouldBe(1);
        stored.Description.ShouldBe("Deskripsi diperbarui oleh penjual.");
    }
}
