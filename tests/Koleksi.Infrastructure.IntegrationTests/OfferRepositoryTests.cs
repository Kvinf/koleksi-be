using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary><c>DynamoOfferRepository</c> against a real table.</summary>
public sealed class OfferRepositoryTests : DynamoTestBase
{
    [DynamoLocalFact]
    public async Task An_offer_survives_the_round_trip()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.AcceptOffers);
        await Table.Listings.AddAsync(listing, Ct);
        var offer = Fixtures.PendingOffer(listing);

        await Table.Offers.AddAsync(offer, Ct);
        var stored = await Table.Offers.FindAsync(offer.Id, Ct);

        stored.ShouldNotBeNull();
        stored.ListingId.ShouldBe(offer.ListingId);
        stored.BuyerId.ShouldBe(offer.BuyerId);
        stored.SellerId.ShouldBe(offer.SellerId);
        stored.Amount.Rupiah.ShouldBe(offer.Amount.Rupiah);
        stored.Message.ShouldBe(offer.Message);
        stored.Status.ShouldBe(OfferStatus.Pending);
    }

    [DynamoLocalFact]
    public async Task A_pending_offer_is_findable_as_the_buyers_active_offer()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.AcceptOffers);
        await Table.Listings.AddAsync(listing, Ct);
        var offer = Fixtures.PendingOffer(listing);
        await Table.Offers.AddAsync(offer, Ct);

        var active = await Table.Offers.FindActiveAsync(listing.Id, Fixtures.BidderId, Ct);

        active.ShouldNotBeNull();
        active.Id.ShouldBe(offer.Id);
    }

    [DynamoLocalFact]
    public async Task No_active_offer_is_reported_for_a_buyer_with_none()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.AcceptOffers);
        await Table.Listings.AddAsync(listing, Ct);

        var active = await Table.Offers.FindActiveAsync(listing.Id, Fixtures.BidderId, Ct);

        active.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task Accepting_an_offer_clears_the_active_offer_pointer()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.AcceptOffers);
        await Table.Listings.AddAsync(listing, Ct);
        var offer = Fixtures.PendingOffer(listing);
        await Table.Offers.AddAsync(offer, Ct);

        offer.Accept(listing.SellerId, Fixtures.Clock);
        await Table.Offers.UpdateAsync(offer, expectedVersion: 0, Ct);

        // The buyer is free to make a fresh offer elsewhere — resolved offers do not
        // block new ones, only an unresolved one does.
        var active = await Table.Offers.FindActiveAsync(listing.Id, Fixtures.BidderId, Ct);
        active.ShouldBeNull();

        var stored = await Table.Offers.FindAsync(offer.Id, Ct);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(OfferStatus.Accepted);
    }

    [DynamoLocalFact]
    public async Task A_stale_version_update_is_rejected()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.AcceptOffers);
        await Table.Listings.AddAsync(listing, Ct);
        var offer = Fixtures.PendingOffer(listing);
        await Table.Offers.AddAsync(offer, Ct);

        offer.Decline(listing.SellerId, Fixtures.Clock);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Table.Offers.UpdateAsync(offer, expectedVersion: 41, Ct));
    }

    [DynamoLocalFact]
    public async Task Offers_list_for_both_the_buyer_and_the_seller()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.AcceptOffers);
        await Table.Listings.AddAsync(listing, Ct);
        var offer = Fixtures.PendingOffer(listing);
        await Table.Offers.AddAsync(offer, Ct);

        var forSeller = await Table.Offers.ListForSellerAsync(listing.SellerId, null, 10, Ct);
        var forBuyer = await Table.Offers.ListForBuyerAsync(Fixtures.BidderId, null, 10, Ct);

        forSeller.Items.ShouldHaveSingleItem().Id.ShouldBe(offer.Id);
        forBuyer.Items.ShouldHaveSingleItem().Id.ShouldBe(offer.Id);
    }
}
