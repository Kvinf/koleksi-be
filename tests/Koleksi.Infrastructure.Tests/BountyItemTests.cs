using Koleksi.Domain.Bounties;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// How a Dicari, its proposals and the ids that link them to an offer and an order are stored: every field survives a
/// round trip, the index entries exist in exactly the states the board and the sweep expect, and rows written before the
/// fields existed still read.
/// </summary>
public sealed class BountyItemTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

    private static Bounty NewBounty() => Bounty.Create(
        Buyer, null, "Dicari PSA 10 Charizard 1999", "cards",
        new Dictionary<string, string> { ["grade"] = "psa-10" }, "Mulus tanpa lecet", ["Segel utuh"],
        Money.FromRupiah(2_000_000), "Bandung", ["bounties/buyer-sub/b1"], 14, Clock);

    private static Bounty Rebuilt(Bounty b, BountyStatus? status = null, string? acceptedProposalId = null, DateTimeOffset? dealDueAt = null) =>
        Bounty.Rehydrate(
            b.Id, b.BuyerId, b.BuyerUsername, b.Title, b.CategoryId, b.Facets, b.Description, b.Conditions, b.MaxBudget,
            b.ShipToCity, b.PhotoKeys, b.CreatedAt, b.ExpiresAt, status ?? b.Status, b.ProposalCount, acceptedProposalId,
            b.RemovedReason, dealDueAt, b.Version);

    [Fact]
    public void A_dicari_round_trips_every_field()
    {
        var bounty = NewBounty();

        var read = BountyItem.ToDomain(BountyItem.ToItem(bounty));

        read.Id.ShouldBe(bounty.Id);
        read.BuyerId.ShouldBe(Buyer);
        read.Title.ShouldBe(bounty.Title);
        read.CategoryId.ShouldBe("cards");
        read.Facets["grade"].ShouldBe("psa-10");
        read.Description.ShouldBe("Mulus tanpa lecet");
        read.Conditions.ShouldBe(["Segel utuh"]);
        read.MaxBudget.Rupiah.ShouldBe(2_000_000);
        read.ShipToCity.ShouldBe("Bandung");
        read.PhotoKeys.ShouldBe(["bounties/buyer-sub/b1"]);
        read.ExpiresAt.ShouldBe(bounty.ExpiresAt);
        read.Status.ShouldBe(BountyStatus.Open);
        read.Code.ShouldBe(BountyCode.For(bounty.Id));
    }

    [Fact]
    public void A_deal_in_flight_round_trips_its_proposal_and_deadline()
    {
        var bounty = NewBounty();
        var due = Clock.UtcNow.AddHours(48);

        var read = BountyItem.ToDomain(BountyItem.ToItem(Rebuilt(bounty, acceptedProposalId: "p-1", dealDueAt: due)));

        read.AcceptedProposalId.ShouldBe("p-1");
        read.DealDueAt.ShouldBe(due);
        read.DealInFlight.ShouldBeTrue();
    }

    [Fact]
    public void An_open_dicari_is_on_the_board_and_on_the_sweeps_clock_at_its_deadline()
    {
        var bounty = NewBounty();

        var item = BountyItem.ToItem(bounty);

        item[DynamoTable.BrowseNewestPk].S.ShouldBe(DynamoTable.BountyOpenPkValue("cards"));
        item[DynamoTable.AuctionEndPk].S.ShouldStartWith("BOUNTYDUE#");
        item[DynamoTable.AuctionEndSk].S.ShouldBe(DynamoTable.BountyDueSkValue(bounty.ExpiresAt, bounty.Id));
    }

    [Fact]
    public void A_dicari_with_a_deal_in_flight_leaves_the_board_but_stays_on_the_clock_at_the_deals_deadline()
    {
        var bounty = NewBounty();
        var due = Clock.UtcNow.AddHours(48);

        var item = BountyItem.ToItem(Rebuilt(bounty, acceptedProposalId: "p-1", dealDueAt: due));

        item.ContainsKey(DynamoTable.BrowseNewestPk).ShouldBeFalse();
        item.ContainsKey(DynamoTable.BrowseNewestSk).ShouldBeFalse();
        item[DynamoTable.AuctionEndSk].S.ShouldBe(DynamoTable.BountyDueSkValue(due, bounty.Id));
    }

    [Theory]
    [InlineData(BountyStatus.Fulfilled)]
    [InlineData(BountyStatus.Closed)]
    [InlineData(BountyStatus.Expired)]
    [InlineData(BountyStatus.Removed)]
    public void A_finished_dicari_carries_no_board_or_clock_entry(BountyStatus status)
    {
        var item = BountyItem.ToItem(Rebuilt(NewBounty(), status));

        foreach (var attribute in BountyItem.SparseIndexAttributes)
        {
            item.ContainsKey(attribute).ShouldBeFalse(attribute);
        }
    }

    [Fact]
    public void A_dicari_is_indexed_for_its_buyer_under_a_prefix_no_order_or_offer_uses()
    {
        var bounty = NewBounty();

        var buyerSk = BountyItem.ToItem(bounty)[DynamoTable.BuyerSk].S;

        buyerSk.ShouldStartWith(DynamoTable.BountyIndexPrefix());
        buyerSk.ShouldNotStartWith("ORDER");
        buyerSk.ShouldNotStartWith("OFFER");
    }

    [Fact]
    public void A_proposal_round_trips_its_price_listing_and_offer_link()
    {
        var proposal = BountyProposal.Rehydrate(
            "prop-1", "bounty-1", Seller, null, "listing-1", Money.FromRupiah(2_400_000), Money.FromRupiah(2_500_000),
            "Kondisi mulus", Clock.UtcNow, ProposalStatus.Accepted, "offer-1", null, 3);

        var read = BountyProposalItem.ToDomain(BountyProposalItem.ToItem(proposal));

        read.Id.ShouldBe("prop-1");
        read.BountyId.ShouldBe("bounty-1");
        read.ListingId.ShouldBe("listing-1");
        read.ProposedPrice.Rupiah.ShouldBe(2_400_000);
        read.ListingPriceAtProposal.Rupiah.ShouldBe(2_500_000);
        read.Note.ShouldBe("Kondisi mulus");
        read.Status.ShouldBe(ProposalStatus.Accepted);
        read.OfferId.ShouldBe("offer-1");
        read.Version.ShouldBe(3);
    }

    [Fact]
    public void A_proposal_over_budget_is_an_upsell_decided_at_read_time_not_stored()
    {
        var proposal = BountyProposal.Rehydrate(
            "prop-1", "bounty-1", Seller, null, "listing-1", Money.FromRupiah(2_400_000), Money.FromRupiah(2_500_000),
            null, Clock.UtcNow, ProposalStatus.Pending, null, null, 0);

        var item = BountyProposalItem.ToItem(proposal);
        var read = BountyProposalItem.ToDomain(item);

        item.Keys.ShouldNotContain(key => key.Contains("upsell", StringComparison.OrdinalIgnoreCase));
        read.IsUpsell(Money.FromRupiah(2_000_000)).ShouldBeTrue();
        read.AmountOverBudget(Money.FromRupiah(2_000_000)).Rupiah.ShouldBe(400_000);
        read.IsUpsell(Money.FromRupiah(2_400_000)).ShouldBeFalse();
    }

    [Fact]
    public void An_offer_from_a_dicari_round_trips_its_origin_and_both_linking_ids()
    {
        var offer = Offer.CreateAcceptedFromBounty(
            "listing-1", Buyer, Seller, Money.FromRupiah(2_400_000), Money.FromRupiah(2_500_000), "bounty-1", "prop-1", Clock);

        var read = OfferItem.ToDomain(OfferItem.ToItem(offer));

        read.Origin.ShouldBe(OfferOrigin.Bounty);
        read.BountyId.ShouldBe("bounty-1");
        read.ProposalId.ShouldBe("prop-1");
        read.Status.ShouldBe(OfferStatus.Accepted);
        read.Id.ShouldNotBe("bounty-1");
        read.Id.ShouldNotBe("prop-1");
    }

    [Fact]
    public void An_offer_stored_before_origins_existed_reads_as_an_ordinary_nego_offer()
    {
        var offer = Offer.Create("L1", Buyer, Seller, Money.FromRupiah(1_500_000), Money.FromRupiah(2_000_000), null, null, Clock);
        var item = OfferItem.ToItem(offer);
        foreach (var attribute in new[] { "origin", "bountyId", "proposalId" })
        {
            item.Remove(attribute);
        }

        var read = OfferItem.ToDomain(item);

        read.Origin.ShouldBe(OfferOrigin.Nego);
        read.BountyId.ShouldBeNull();
        read.ProposalId.ShouldBeNull();
    }

    [Fact]
    public void An_order_from_a_dicari_round_trips_its_source_and_dicari_id_and_an_older_order_has_none()
    {
        var courier = new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3);
        var ship = new ShippingAddress("Dwi", "0812", "Jl. Tebet", "Jakarta Selatan", "DKI Jakarta", "12810");
        var line = new OrderLine("L1", "PSA 10 Charizard", null, Money.FromRupiah(2_400_000), 1);

        var order = Order.Create(
            Buyer, Seller, [line], ship, courier, Clock, OrderSource.BountyOffer, Clock.UtcNow.AddMinutes(15), "offer-1", "bounty-1");
        var item = OrderItem.ToItem(order);

        var read = OrderItem.ToDomain(item);
        read.Source.ShouldBe(OrderSource.BountyOffer);
        read.SourceReference.ShouldBe("offer-1");
        read.BountyId.ShouldBe("bounty-1");

        item.Remove("bountyId");
        OrderItem.ToDomain(item).BountyId.ShouldBeNull();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
