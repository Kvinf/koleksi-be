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
    public async Task An_accepted_offer_is_still_the_buyers_open_deal_until_it_is_resolved()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.AcceptOffers);
        await Table.Listings.AddAsync(listing, Ct);
        var offer = Fixtures.PendingOffer(listing);
        await Table.Offers.AddAsync(offer, Ct);

        offer.Accept(listing.SellerId, Fixtures.Clock);
        await Table.Offers.UpdateAsync(offer, expectedVersion: 0, Ct);

        // Accepted but unpaid: the buyer cannot stack a second offer beside it.
        (await Table.Offers.FindActiveAsync(listing.Id, Fixtures.BidderId, Ct)).ShouldNotBeNull();
        (await Table.Offers.FindAsync(offer.Id, Ct))!.Status.ShouldBe(OfferStatus.Accepted);

        // Checked out: the order may still die unpaid and reopen the deal (D-08), so the buyer's
        // pointer stays and they still cannot stack a second offer. The order id is kept.
        offer.MarkCheckedOut(Fixtures.BidderId, "order-1", Fixtures.Clock);
        await Table.Offers.UpdateAsync(offer, expectedVersion: 1, Ct);

        (await Table.Offers.FindActiveAsync(listing.Id, Fixtures.BidderId, Ct)).ShouldNotBeNull();
        var stored = (await Table.Offers.FindAsync(offer.Id, Ct))!;
        stored.Status.ShouldBe(OfferStatus.CheckedOut);
        stored.OrderId.ShouldBe("order-1");

        // The unpaid order dies: the deal is the buyer's again, still open.
        offer.ReopenAfterUnpaidOrder(Fixtures.Clock);
        await Table.Offers.UpdateAsync(offer, expectedVersion: 2, Ct);

        (await Table.Offers.FindActiveAsync(listing.Id, Fixtures.BidderId, Ct)).ShouldNotBeNull();
        (await Table.Offers.FindAsync(offer.Id, Ct))!.Status.ShouldBe(OfferStatus.Accepted);

        // Resolved: its deadline passes, the pointer goes, and the buyer may make a fresh offer.
        offer.Expire(new FixedClock { UtcNow = offer.ExpiresAt }).ShouldBeTrue();
        await Table.Offers.UpdateAsync(offer, expectedVersion: 3, Ct);

        (await Table.Offers.FindActiveAsync(listing.Id, Fixtures.BidderId, Ct)).ShouldBeNull();
        (await Table.Offers.FindAsync(offer.Id, Ct))!.Status.ShouldBe(OfferStatus.Expired);
    }

    [DynamoLocalFact]
    public async Task A_counter_is_stored_beside_the_buyers_original_amount()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.AcceptOffers);
        await Table.Listings.AddAsync(listing, Ct);
        var offer = Fixtures.PendingOffer(listing);
        await Table.Offers.AddAsync(offer, Ct);

        offer.Counter(Money.FromRupiah(listing.Price.Rupiah - 100_000), listing.SellerId, Fixtures.Clock);
        await Table.Offers.UpdateAsync(offer, expectedVersion: 0, Ct);

        var stored = (await Table.Offers.FindAsync(offer.Id, Ct))!;
        stored.Status.ShouldBe(OfferStatus.Countered);
        stored.Amount.Rupiah.ShouldBe(2_000_000);
        stored.CounterAmount!.Value.Rupiah.ShouldBe(listing.Price.Rupiah - 100_000);
        (await Table.Offers.FindActiveAsync(listing.Id, Fixtures.BidderId, Ct)).ShouldNotBeNull();
    }

    [DynamoLocalFact]
    public async Task Two_simultaneous_checkouts_of_one_accepted_offer_cannot_both_claim_it()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.AcceptOffers);
        await Table.Listings.AddAsync(listing, Ct);
        var offer = Fixtures.PendingOffer(listing);
        await Table.Offers.AddAsync(offer, Ct);
        offer.Accept(listing.SellerId, Fixtures.Clock);
        await Table.Offers.UpdateAsync(offer, expectedVersion: 0, Ct);

        // Each checkout reads the offer, then tries to spend it with a write conditional on
        // the version it read. The table lets exactly one of them through. A checkout that reads
        // after the winner committed is refused by the domain instead ("already used"), which is
        // just as much a loss: which refusal a loser meets depends on timing.
        var claims = Enumerable.Range(0, 8).Select(async attempt =>
        {
            var copy = (await Table.Offers.FindAsync(offer.Id, Ct))!;
            var version = copy.Version;
            try
            {
                copy.MarkCheckedOut(Fixtures.BidderId, $"order-{attempt}", Fixtures.Clock);
                await Table.Offers.UpdateAsync(copy, version, Ct);
                return true;
            }
            catch (Exception lost) when (lost is ConcurrencyConflictException or DomainException { Code: "offer.already_used" })
            {
                return false;
            }
        });

        var results = await Task.WhenAll(claims);

        results.Count(won => won).ShouldBe(1);
        (await Table.Offers.FindAsync(offer.Id, Ct))!.Status.ShouldBe(OfferStatus.CheckedOut);
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
