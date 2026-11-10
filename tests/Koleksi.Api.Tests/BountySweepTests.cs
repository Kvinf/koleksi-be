using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Features.Bounties;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Standing;
using Koleksi.Application.Features.Sweeps;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// What the sweep does with a Dicari whose clock ran out, and how a Dicari follows its deal: expiry, a deal that falls
/// through, a deal that is paid.
/// </summary>
public sealed class BountySweepTests
{
    private const string Buyer = "buyer-bounty-sweep";

    private readonly MutableClock _clock = new();
    private readonly FakeBountyRepository _bounties = new();
    private readonly FakeOfferRepository _offers = new();
    private readonly FakeOrderRepository _orders = new();
    private readonly FakeRealtimeBroadcaster _realtime = new();

    private ExpireDueBountiesHandler Handler => new(
        _bounties,
        new BountyDeals(_bounties, _offers, _orders, new BountyNotifier(_realtime, NullLogger<BountyNotifier>.Instance), _clock, NullLogger<BountyDeals>.Instance),
        _clock,
        NullLogger<ExpireDueBountiesHandler>.Instance);

    [Fact]
    public async Task A_dicari_nobody_dealt_on_expires_and_its_pending_proposals_lapse()
    {
        var (bounty, proposal, _) = await PostWithPendingProposalAsync();
        _clock.UtcNow = bounty.ExpiresAt + TimeSpan.FromMinutes(1);

        var result = await Handler.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Settled.ShouldBe(1);
        (await _bounties.FindAsync(bounty.Id, CancellationToken.None))!.Status.ShouldBe(BountyStatus.Expired);
        var lapsed = await _bounties.FindProposalAsync(bounty.Id, proposal.Id, CancellationToken.None);
        lapsed!.Status.ShouldBe(ProposalStatus.Lapsed);
        lapsed.LapseReason.ShouldBe(BountyDeals.Reasons.BountyEnded);
    }

    [Fact]
    public async Task A_dicari_that_is_not_due_yet_is_left_alone()
    {
        var (bounty, _, _) = await PostWithPendingProposalAsync();
        _clock.UtcNow = bounty.ExpiresAt - TimeSpan.FromDays(1);

        var result = await Handler.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Settled.ShouldBe(0);
        (await _bounties.FindAsync(bounty.Id, CancellationToken.None))!.Status.ShouldBe(BountyStatus.Open);
    }

    [Fact]
    public async Task A_deal_nobody_checked_out_falls_through_and_the_dicari_opens_again()
    {
        var (bounty, proposal, listing) = await PostWithPendingProposalAsync();
        var offer = await AcceptAsync(bounty, proposal, listing);
        _clock.UtcNow = bounty.DealDueAt!.Value + TimeSpan.FromMinutes(1);

        var result = await Handler.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Settled.ShouldBe(1);
        var reopened = (await _bounties.FindAsync(bounty.Id, CancellationToken.None))!;
        reopened.Status.ShouldBe(BountyStatus.Open);
        reopened.AcceptedProposalId.ShouldBeNull();
        reopened.DealInFlight.ShouldBeFalse();
        var lapsed = (await _bounties.FindProposalAsync(bounty.Id, proposal.Id, CancellationToken.None))!;
        lapsed.Status.ShouldBe(ProposalStatus.Lapsed);
        lapsed.LapseReason.ShouldBe(BountyDeals.Reasons.DealFellThrough);
    }

    [Fact]
    public async Task A_deal_whose_order_was_paid_fulfils_the_dicari()
    {
        var (bounty, proposal, listing) = await PostWithPendingProposalAsync();
        var offer = await AcceptAsync(bounty, proposal, listing);
        var order = OrderFor(offer, bounty);
        order.ConfirmPayment(order.GrandTotal, _clock);
        _orders.Seed(order);
        offer.MarkCheckedOut(Buyer, order.Id, _clock);
        _offers.Seed(offer);
        _clock.UtcNow = bounty.DealDueAt!.Value + TimeSpan.FromMinutes(1);

        await Handler.RunAsync(new SweepBudget(), CancellationToken.None);

        (await _bounties.FindAsync(bounty.Id, CancellationToken.None))!.Status.ShouldBe(BountyStatus.Fulfilled);
    }

    [Fact]
    public async Task A_buyer_who_is_paying_right_now_is_not_cut_off_by_the_sweep()
    {
        var (bounty, proposal, listing) = await PostWithPendingProposalAsync();
        var offer = await AcceptAsync(bounty, proposal, listing);
        var order = OrderFor(offer, bounty, dueAt: bounty.DealDueAt!.Value + TimeSpan.FromMinutes(15));
        _orders.Seed(order);
        offer.MarkCheckedOut(Buyer, order.Id, _clock);
        _offers.Seed(offer);
        _clock.UtcNow = bounty.DealDueAt!.Value + TimeSpan.FromMinutes(1);

        await Handler.RunAsync(new SweepBudget(), CancellationToken.None);

        var still = (await _bounties.FindAsync(bounty.Id, CancellationToken.None))!;
        still.Status.ShouldBe(BountyStatus.Open);
        still.DealInFlight.ShouldBeTrue();
    }

    [Fact]
    public async Task A_deal_that_falls_through_after_the_dicaris_own_deadline_expires_it_instead_of_reopening()
    {
        var bounty = NewBounty(durationDays: 7);
        _bounties.Seed(bounty);
        var listing = TestData.ActiveListing(price: Money.FromRupiah(2_500_000));
        var proposal = BountyProposal.Create(bounty, listing, listing.SellerId, Money.FromRupiah(2_000_000), null, null, _clock);
        await _bounties.AddProposalAsync(proposal, CancellationToken.None);
        _clock.UtcNow = bounty.ExpiresAt - TimeSpan.FromHours(1);
        var offer = await AcceptAsync(bounty, proposal, listing);
        _clock.UtcNow = bounty.DealDueAt!.Value + TimeSpan.FromMinutes(1);

        await Handler.RunAsync(new SweepBudget(), CancellationToken.None);

        (await _bounties.FindAsync(bounty.Id, CancellationToken.None))!.Status.ShouldBe(BountyStatus.Expired);
    }

    [Fact]
    public void An_unpaid_dicari_order_is_a_commitment_that_counts_like_an_unpaid_accepted_offer()
    {
        var offer = Offer.CreateAcceptedFromBounty(
            "listing-1", Buyer, "seller-1", Money.FromRupiah(2_000_000), Money.FromRupiah(2_500_000), "bounty-1", "prop-1", _clock);
        var bounty = NewBounty();
        var order = OrderFor(offer, bounty);

        var violation = OrderViolations.Of(order);

        violation.ShouldNotBeNull();
        violation.Value.Type.ShouldBe(ViolationType.AcceptedOfferNotPaid);
        violation.Value.Reference.ShouldBe($"bounty:{offer.Id}");
    }

    [Fact]
    public async Task Paying_for_the_deal_lapses_the_proposals_nobody_accepted()
    {
        var (bounty, proposal, listing) = await PostWithPendingProposalAsync();
        var other = await AddProposalFromAnotherSellerAsync(bounty);
        var offer = await AcceptAsync(bounty, proposal, listing);
        var order = OrderFor(offer, bounty);
        order.ConfirmPayment(order.GrandTotal, _clock);
        _orders.Seed(order);

        await Deals.FulfilAsync(bounty.Id, CancellationToken.None);

        (await _bounties.FindAsync(bounty.Id, CancellationToken.None))!.Status.ShouldBe(BountyStatus.Fulfilled);
        var settled = (await _bounties.FindProposalAsync(bounty.Id, other.Id, CancellationToken.None))!;
        settled.Status.ShouldBe(ProposalStatus.Lapsed);
        settled.LapseReason.ShouldBe(BountyDeals.Reasons.BountyEnded);
    }

    [Fact]
    public async Task A_cancelled_dicari_order_hands_the_dicari_back_at_once()
    {
        var (bounty, proposal, listing) = await PostWithPendingProposalAsync();
        var offer = await AcceptAsync(bounty, proposal, listing);
        var order = OrderFor(offer, bounty);
        _orders.Seed(order);

        // Well before DealDueAt: only the dead order is the reason.
        await Deals.ReleaseAfterOrderDiedAsync(order, CancellationToken.None);

        var reopened = (await _bounties.FindAsync(bounty.Id, CancellationToken.None))!;
        reopened.Status.ShouldBe(BountyStatus.Open);
        reopened.DealInFlight.ShouldBeFalse();
        (await _bounties.FindProposalAsync(bounty.Id, proposal.Id, CancellationToken.None))!.Status.ShouldBe(ProposalStatus.Lapsed);
    }

    [Fact]
    public async Task A_retired_dicari_offer_can_no_longer_be_paid()
    {
        var (bounty, proposal, listing) = await PostWithPendingProposalAsync();
        var offer = await AcceptAsync(bounty, proposal, listing);

        offer.CancelUnusedBountyOffer();

        offer.Status.ShouldBe(OfferStatus.Expired);
    }

    private BountyDeals Deals => new(
        _bounties, _offers, _orders, new BountyNotifier(_realtime, NullLogger<BountyNotifier>.Instance), _clock, NullLogger<BountyDeals>.Instance);

    private async Task<BountyProposal> AddProposalFromAnotherSellerAsync(Bounty bounty)
    {
        var listing = Listing.CreateDraft(
            "another-seller", TestData.CardsCategoryId, "PSA 10 Charizard another copy", Money.FromRupiah(2_400_000),
            ListingFormat.FixedPrice, "PSA 10", "Surabaya", _clock);
        listing.SetPhotos([ListingPhoto.Create("listing/photo-two.jpg", 1200, 1200)], _clock);
        listing.Publish(_clock);
        var proposal = BountyProposal.Create(bounty, listing, "another-seller", Money.FromRupiah(2_100_000), null, null, _clock);
        await _bounties.AddProposalAsync(proposal, CancellationToken.None);
        return proposal;
    }

    // ------------------------------------------------------------------ helpers

    private Bounty NewBounty(int durationDays = 14) => Bounty.Create(
        Buyer, null, "Dicari PSA 10 Charizard 1999", TestData.CardsCategoryId, new Dictionary<string, string>(), null, ["PSA 10"],
        Money.FromRupiah(2_000_000), "Bandung", [], durationDays, _clock);

    private async Task<(Bounty Bounty, BountyProposal Proposal, Listing Listing)> PostWithPendingProposalAsync()
    {
        var bounty = NewBounty();
        _bounties.Seed(bounty);
        var listing = TestData.ActiveListing(price: Money.FromRupiah(2_500_000));
        var proposal = BountyProposal.Create(bounty, listing, listing.SellerId, Money.FromRupiah(2_000_000), null, null, _clock);
        await _bounties.AddProposalAsync(proposal, CancellationToken.None);
        return (bounty, proposal, listing);
    }

    /// <summary>Accepts the proposal the way the handler does and stores every row it touches.</summary>
    private async Task<Offer> AcceptAsync(Bounty bounty, BountyProposal proposal, Listing listing)
    {
        var bountyVersion = bounty.Version;
        var proposalVersion = proposal.Version;
        var offer = proposal.Accept(bounty, Buyer, listing, null, _clock);
        await _bounties.UpdateAsync(bounty, bountyVersion, CancellationToken.None);
        await _offers.AddAsync(offer, CancellationToken.None);
        await _bounties.UpdateProposalAsync(proposal, proposalVersion, CancellationToken.None);
        return offer;
    }

    private Order OrderFor(Offer offer, Bounty bounty, DateTimeOffset? dueAt = null) => Order.Create(
        Buyer,
        offer.SellerId,
        [new OrderLine(offer.ListingId, "PSA 10 Charizard", null, offer.CurrentAmount, 1)],
        new ShippingAddress("Pembeli Uji", "081234567890", "Jl. Merdeka No. 1", "Bandung", "Jawa Barat", "40111"),
        FlatRateCouriers.Find("jne")!,
        _clock,
        OrderSource.BountyOffer,
        dueAt ?? _clock.UtcNow + Order.PaymentWindow,
        offer.Id,
        bounty.Id);
}
