using Koleksi.Domain.Bounties;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class BountyTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";
    private const string OtherSeller = "other-seller-sub";
    private static readonly Money Budget = Money.FromRupiah(1_000_000);
    private static readonly IReadOnlyDictionary<string, string> NoFacets = new Dictionary<string, string>();

    private static Bounty Open(TestClock clock, int days = 14) => Bounty.Create(
        Buyer, "kolektor", "PSA 10 Charizard Base Set", "cards", NoFacets, "Cari yang mulus.", ["PSA 10", "PSA 9"],
        Budget, "Bandung", null, days, clock);

    private static Listing Live(
        TestClock clock,
        string seller = Seller,
        long price = 1_400_000,
        string category = "cards",
        ListingFormat format = ListingFormat.AcceptOffers,
        int stock = 1)
    {
        var listing = Listing.CreateDraft(
            seller, category, "PSA 10 Charizard Base Set Holo", Money.FromRupiah(price), format, "PSA 10", "Jakarta", clock);
        listing.SetPhotos([ListingPhoto.Create("listings/s/one", 1200, 1200)], clock);
        if (format != ListingFormat.Auction)
        {
            listing.SetStock(stock, clock);
        }

        listing.Publish(clock);
        return listing;
    }

    private static string CodeOf(Action act) => Should.Throw<DomainException>(act).Code;

    // ─── the Dicari ─────────────────────────────────────────────────────────

    [Fact]
    public void A_new_dicari_is_open_for_the_chosen_number_of_days()
    {
        var clock = TestClock.AtEpoch();

        var bounty = Open(clock, days: 30);

        bounty.Status.ShouldBe(BountyStatus.Open);
        bounty.ExpiresAt.ShouldBe(clock.UtcNow.AddDays(30));
        bounty.ProposalCount.ShouldBe(0);
        bounty.AcceptsProposals(clock).ShouldBeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(0)]
    public void Only_seven_fourteen_or_thirty_days_are_allowed(int days) =>
        CodeOf(() => Open(TestClock.AtEpoch(), days)).ShouldBe("bounty.invalid_duration");

    [Fact]
    public void The_code_comes_from_the_dicari_id_alone_and_is_not_an_order_number()
    {
        var bounty = Open(TestClock.AtEpoch());

        bounty.Code.ShouldBe($"DCR-{bounty.Id[^6..]}");
        bounty.Code.ShouldStartWith("DCR-");
        BountyCode.For(bounty.Id).ShouldBe(bounty.Code);
    }

    [Theory]
    [InlineData("abcd")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_title_under_five_characters_is_refused(string title) =>
        CodeOf(() => Bounty.Create(
            Buyer, null, title, "cards", NoFacets, null, ["PSA 10"], Budget, "Bandung", null, 7, TestClock.AtEpoch()))
            .ShouldBe("bounty.invalid_title");

    [Fact]
    public void A_title_over_one_hundred_twenty_characters_is_refused() =>
        CodeOf(() => Bounty.Create(
            Buyer, null, new string('a', 121), "cards", NoFacets, null, ["PSA 10"], Budget, "Bandung", null, 7, TestClock.AtEpoch()))
            .ShouldBe("bounty.invalid_title");

    [Fact]
    public void A_description_over_one_thousand_characters_is_refused() =>
        CodeOf(() => Bounty.Create(
            Buyer, null, "Charizard mulus", "cards", NoFacets, new string('a', 1001), ["PSA 10"], Budget, "Bandung", null, 7, TestClock.AtEpoch()))
            .ShouldBe("bounty.description_too_long");

    [Fact]
    public void A_zero_budget_is_refused() =>
        CodeOf(() => Bounty.Create(
            Buyer, null, "Charizard mulus", "cards", NoFacets, null, ["PSA 10"], Money.Zero, "Bandung", null, 7, TestClock.AtEpoch()))
            .ShouldBe("bounty.invalid_budget");

    [Fact]
    public void At_least_one_accepted_condition_is_required() =>
        CodeOf(() => Bounty.Create(
            Buyer, null, "Charizard mulus", "cards", NoFacets, null, [], Budget, "Bandung", null, 7, TestClock.AtEpoch()))
            .ShouldBe("bounty.invalid_condition");

    [Fact]
    public void Repeated_conditions_are_stored_once()
    {
        var bounty = Bounty.Create(
            Buyer, null, "Charizard mulus", "cards", NoFacets, null, ["PSA 10", "psa 10", "PSA 9"], Budget, "Bandung", null, 7, TestClock.AtEpoch());

        bounty.Conditions.ShouldBe(["PSA 10", "PSA 9"]);
    }

    [Fact]
    public void The_ship_to_city_is_required() =>
        CodeOf(() => Bounty.Create(
            Buyer, null, "Charizard mulus", "cards", NoFacets, null, ["PSA 10"], Budget, "  ", null, 7, TestClock.AtEpoch()))
            .ShouldBe("bounty.invalid_city");

    [Fact]
    public void At_most_three_reference_photos_are_allowed() =>
        CodeOf(() => Bounty.Create(
            Buyer, null, "Charizard mulus", "cards", NoFacets, null, ["PSA 10"], Budget, "Bandung", ["a", "b", "c", "d"], 7, TestClock.AtEpoch()))
            .ShouldBe("bounty.too_many_photos");

    [Theory]
    [InlineData("Hubungi 081234567890 ya")]
    [InlineData("wa 0812-3456-7890")]
    [InlineData("telepon +62 812 3456 7890")]
    [InlineData("kirim ke budi@example.com")]
    [InlineData("lihat https://tokopedia.com/toko")]
    [InlineData("cek www.toko.co.id")]
    [InlineData("chat di wa.me/628123456789")]
    public void Phone_numbers_emails_and_links_are_refused_in_the_description(string text) =>
        CodeOf(() => Bounty.Create(
            Buyer, null, "Charizard mulus", "cards", NoFacets, text, ["PSA 10"], Budget, "Bandung", null, 7, TestClock.AtEpoch()))
            .ShouldBe("bounty.contact_info");

    [Fact]
    public void A_contact_detail_in_the_title_is_refused_too() =>
        CodeOf(() => Bounty.Create(
            Buyer, null, "Charizard wa 081234567890", "cards", NoFacets, null, ["PSA 10"], Budget, "Bandung", null, 7, TestClock.AtEpoch()))
            .ShouldBe("bounty.contact_info");

    [Theory]
    [InlineData("Budget sekitar Rp 15.000.000, kondisi PSA 10 saja.")]
    [InlineData("Terbit 1999, set pertama, edisi 1st.")]
    [InlineData("Harga 1.250.000 sampai 2.000.000")]
    public void Ordinary_text_and_prices_are_not_mistaken_for_contact_details(string text)
    {
        var bounty = Bounty.Create(
            Buyer, null, "Charizard mulus", "cards", NoFacets, text, ["PSA 10"], Budget, "Bandung", null, 7, TestClock.AtEpoch());

        bounty.Description.ShouldBe(text);
    }

    [Fact]
    public void Editing_changes_the_allowed_fields_and_keeps_the_category_when_proposals_exist()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        bounty.NoteProposalAdded();

        bounty.Edit(Buyer, "Charizard base set mulus", "Baru", Money.FromRupiah(2_000_000), ["PSA 10"], null, null, null, clock);

        bounty.Title.ShouldBe("Charizard base set mulus");
        bounty.MaxBudget.ShouldBe(Money.FromRupiah(2_000_000));
        bounty.Conditions.ShouldBe(["PSA 10"]);
        bounty.Version.ShouldBe(1);
    }

    [Fact]
    public void The_category_is_locked_once_any_proposal_exists()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        bounty.NoteProposalAdded();

        CodeOf(() => bounty.Edit(Buyer, "Charizard base set", null, Budget, ["PSA 10"], null, "sneakers", NoFacets, clock))
            .ShouldBe("bounty.category_locked");
        bounty.CategoryId.ShouldBe("cards");
    }

    [Fact]
    public void The_category_can_change_before_the_first_proposal()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);

        bounty.Edit(Buyer, "Charizard base set", null, Budget, ["PSA 10"], null, "sneakers", NoFacets, clock);

        bounty.CategoryId.ShouldBe("sneakers");
    }

    [Fact]
    public void A_refused_edit_changes_nothing()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);

        CodeOf(() => bounty.Edit(Buyer, "Charizard mulus", "hubungi 081234567890", Budget, ["PSA 10"], null, null, null, clock))
            .ShouldBe("bounty.contact_info");

        bounty.Title.ShouldBe("PSA 10 Charizard Base Set");
        bounty.Version.ShouldBe(0);
    }

    [Fact]
    public void Only_the_owner_can_edit_or_close()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);

        CodeOf(() => bounty.Edit(Seller, "Charizard mulus", null, Budget, ["PSA 10"], null, null, null, clock)).ShouldBe("bounty.forbidden");
        CodeOf(() => bounty.Close(Seller, clock)).ShouldBe("bounty.forbidden");
    }

    [Fact]
    public void A_closed_dicari_cannot_be_edited_or_take_proposals()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        bounty.Close(Buyer, clock);

        bounty.Status.ShouldBe(BountyStatus.Closed);
        bounty.AcceptsProposals(clock).ShouldBeFalse();
        CodeOf(() => bounty.Edit(Buyer, "Charizard mulus", null, Budget, ["PSA 10"], null, null, null, clock)).ShouldBe("bounty.not_open");
        CodeOf(() => bounty.Close(Buyer, clock)).ShouldBe("bounty.not_open");
    }

    [Fact]
    public void An_open_dicari_past_its_deadline_reads_and_becomes_expired()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock, days: 7);
        clock.Advance(TimeSpan.FromDays(7));

        bounty.EffectiveStatus(clock).ShouldBe(BountyStatus.Expired);
        bounty.AcceptsProposals(clock).ShouldBeFalse();
        bounty.Expire(clock).ShouldBeTrue();
        bounty.Status.ShouldBe(BountyStatus.Expired);
        bounty.Expire(clock).ShouldBeFalse();
    }

    [Fact]
    public void Staff_can_remove_a_dicari_but_not_a_fulfilled_one()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        bounty.Remove("selling a fake");

        bounty.Status.ShouldBe(BountyStatus.Removed);
        bounty.RemovedReason.ShouldBe("selling a fake");
        bounty.AcceptsProposals(clock).ShouldBeFalse();

        var done = Open(clock);
        var doneListing = Live(clock);
        var proposal = BountyProposal.Create(done, doneListing, Seller, Money.FromRupiah(900_000), null, "penjual", clock);
        proposal.Accept(done, Buyer, doneListing, "kolektor", clock);
        done.Fulfil();
        CodeOf(() => done.Remove("late")).ShouldBe("bounty.already_fulfilled");
    }

    // ─── proposals ──────────────────────────────────────────────────────────

    [Fact]
    public void A_proposal_on_an_eligible_listing_starts_pending_with_its_own_id()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);

        var proposal = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), "  Mulus, ada kotak  ", "penjual", clock);

        proposal.Status.ShouldBe(ProposalStatus.Pending);
        proposal.Id.ShouldNotBe(bounty.Id);
        proposal.BountyId.ShouldBe(bounty.Id);
        proposal.ListingId.ShouldBe(listing.Id);
        proposal.ListingPriceAtProposal.ShouldBe(listing.Price);
        proposal.Note.ShouldBe("Mulus, ada kotak");
        proposal.OfferId.ShouldBeNull();
    }

    [Fact]
    public void A_seller_cannot_propose_on_their_own_dicari()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var own = Live(clock, seller: Buyer);

        CodeOf(() => BountyProposal.Create(bounty, own, Buyer, Money.FromRupiah(900_000), null, null, clock))
            .ShouldBe("bounty.self_proposal");
    }

    [Fact]
    public void The_listing_must_belong_to_the_proposing_seller()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => BountyProposal.Create(Open(clock), Live(clock, seller: OtherSeller), Seller, Money.FromRupiah(900_000), null, null, clock))
            .ShouldBe("bounty.listing_not_yours");
    }

    [Fact]
    public void An_auction_cannot_back_a_proposal()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => BountyProposal.Create(Open(clock), Live(clock, format: ListingFormat.Auction), Seller, Money.FromRupiah(900_000), null, null, clock))
            .ShouldBe("bounty.listing_is_auction");
    }

    [Fact]
    public void The_listing_must_be_in_the_same_category()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => BountyProposal.Create(Open(clock), Live(clock, category: "sneakers"), Seller, Money.FromRupiah(900_000), null, null, clock))
            .ShouldBe("bounty.category_mismatch");
    }

    [Fact]
    public void The_listing_must_be_active()
    {
        var clock = TestClock.AtEpoch();
        var listing = Live(clock);
        listing.Archive(clock);

        CodeOf(() => BountyProposal.Create(Open(clock), listing, Seller, Money.FromRupiah(900_000), null, null, clock))
            .ShouldBe("bounty.listing_unavailable");
    }

    [Fact]
    public void The_proposed_price_cannot_exceed_the_listing_price()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => BountyProposal.Create(Open(clock), Live(clock, price: 1_400_000), Seller, Money.FromRupiah(1_400_001), null, null, clock))
            .ShouldBe("bounty.price_above_listing");
    }

    [Fact]
    public void A_zero_price_is_refused()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => BountyProposal.Create(Open(clock), Live(clock), Seller, Money.Zero, null, null, clock))
            .ShouldBe("bounty.invalid_price");
    }

    [Fact]
    public void A_note_cannot_carry_contact_details_or_be_too_long()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);

        CodeOf(() => BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), "chat saya 081234567890", null, clock))
            .ShouldBe("bounty.contact_info");
        CodeOf(() => BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), new string('a', 501), null, clock))
            .ShouldBe("bounty.note_too_long");
    }

    [Fact]
    public void A_closed_dicari_takes_no_proposals()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        bounty.Close(Buyer, clock);

        CodeOf(() => BountyProposal.Create(bounty, Live(clock), Seller, Money.FromRupiah(900_000), null, null, clock))
            .ShouldBe("bounty.not_open");
    }

    // ─── upsell ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_proposal_above_the_budget_is_an_upsell_and_one_at_or_below_is_not()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock, price: 1_400_000);

        var upsell = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(1_250_000), null, null, clock);
        var atBudget = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(1_000_000), null, null, clock);

        upsell.IsUpsell(bounty.MaxBudget).ShouldBeTrue();
        upsell.AmountOverBudget(bounty.MaxBudget).ShouldBe(Money.FromRupiah(250_000));
        atBudget.IsUpsell(bounty.MaxBudget).ShouldBeFalse();
        atBudget.AmountOverBudget(bounty.MaxBudget).ShouldBe(Money.Zero);
    }

    [Fact]
    public void Accepting_an_upsell_makes_the_proposed_price_the_checkout_item_and_leaves_the_budget_alone()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock, price: 1_400_000);
        var proposal = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(1_250_000), null, "penjual", clock);
        proposal.IsUpsell(bounty.MaxBudget).ShouldBeTrue();

        var offer = proposal.Accept(bounty, Buyer, listing, "kolektor", clock);

        offer.Status.ShouldBe(OfferStatus.Accepted);
        offer.CurrentAmount.ShouldBe(Money.FromRupiah(1_250_000));
        offer.AskingPrice.ShouldBe(listing.Price);
        offer.ListingId.ShouldBe(listing.Id);
        offer.BuyerId.ShouldBe(Buyer);
        offer.SellerId.ShouldBe(Seller);
        offer.ExpiresAt.ShouldBe(clock.UtcNow + Offer.CheckoutWindow);
        offer.Origin.ShouldBe(OfferOrigin.Bounty);
        offer.BountyId.ShouldBe(bounty.Id);
        offer.ProposalId.ShouldBe(proposal.Id);

        bounty.MaxBudget.ShouldBe(Budget);
        bounty.AcceptedProposalId.ShouldBe(proposal.Id);
        bounty.Status.ShouldBe(BountyStatus.Open);
        proposal.Status.ShouldBe(ProposalStatus.Accepted);
        proposal.OfferId.ShouldBe(offer.Id);
    }

    [Fact]
    public void Accepting_a_proposal_within_budget_behaves_the_same()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);
        var proposal = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock);

        var offer = proposal.Accept(bounty, Buyer, listing, null, clock);

        offer.CurrentAmount.ShouldBe(Money.FromRupiah(900_000));
        offer.Status.ShouldBe(OfferStatus.Accepted);
        proposal.IsUpsell(bounty.MaxBudget).ShouldBeFalse();
    }

    [Fact]
    public void The_dicari_bounty_and_proposal_and_offer_each_have_their_own_id()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);
        var proposal = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock);

        var offer = proposal.Accept(bounty, Buyer, listing, null, clock);

        new[] { bounty.Id, proposal.Id, offer.Id }.Distinct().Count().ShouldBe(3);
    }

    // ─── accepting ──────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_owner_can_accept()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);
        var proposal = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock);

        CodeOf(() => proposal.Accept(bounty, OtherSeller, listing, null, clock)).ShouldBe("bounty.forbidden");
        proposal.Status.ShouldBe(ProposalStatus.Pending);
    }

    [Fact]
    public void A_dicari_with_a_deal_in_flight_takes_no_new_proposals_and_cannot_be_closed()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);
        var proposal = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock);
        proposal.Accept(bounty, Buyer, listing, null, clock);

        bounty.AcceptsProposals(clock).ShouldBeFalse();
        CodeOf(() => BountyProposal.Create(bounty, Live(clock, seller: OtherSeller), OtherSeller, Money.FromRupiah(900_000), null, null, clock))
            .ShouldBe("bounty.deal_in_progress");
        CodeOf(() => bounty.Close(Buyer, clock)).ShouldBe("bounty.deal_in_progress");
    }

    [Fact]
    public void A_second_proposal_cannot_be_accepted_while_a_deal_is_in_flight()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);
        var first = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock);
        var otherListing = Live(clock, seller: OtherSeller);
        var second = BountyProposal.Create(bounty, otherListing, OtherSeller, Money.FromRupiah(950_000), null, null, clock);
        first.Accept(bounty, Buyer, listing, null, clock);

        CodeOf(() => second.Accept(bounty, Buyer, otherListing, null, clock)).ShouldBe("bounty.deal_in_progress");
    }

    [Fact]
    public void A_proposal_whose_listing_sold_or_was_repriced_below_it_cannot_be_accepted()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock, price: 1_400_000);
        var proposal = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(1_300_000), null, null, clock);

        listing.ChangePrice(Money.FromRupiah(1_100_000), clock);

        CodeOf(() => proposal.Accept(bounty, Buyer, listing, null, clock)).ShouldBe("bounty.proposal_price_stale");
    }

    [Fact]
    public void A_declined_withdrawn_or_lapsed_proposal_cannot_be_accepted()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);

        var declined = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock);
        declined.Decline(Buyer, bounty);
        CodeOf(() => declined.Accept(bounty, Buyer, listing, null, clock)).ShouldBe("bounty.proposal_not_pending");

        var withdrawn = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock);
        withdrawn.Withdraw(Seller);
        withdrawn.Status.ShouldBe(ProposalStatus.Withdrawn);
        CodeOf(() => withdrawn.Accept(bounty, Buyer, listing, null, clock)).ShouldBe("bounty.proposal_not_pending");

        var lapsed = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock);
        lapsed.Lapse("bounty.listing_unavailable");
        lapsed.Status.ShouldBe(ProposalStatus.Lapsed);
        lapsed.LapseReason.ShouldBe("bounty.listing_unavailable");
        CodeOf(() => lapsed.Accept(bounty, Buyer, listing, null, clock)).ShouldBe("bounty.proposal_not_pending");
    }

    [Fact]
    public void Only_the_proposing_seller_can_withdraw()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var proposal = BountyProposal.Create(bounty, Live(clock), Seller, Money.FromRupiah(900_000), null, null, clock);

        CodeOf(() => proposal.Withdraw(OtherSeller)).ShouldBe("bounty.forbidden");
    }

    // ─── what happens to the deal ───────────────────────────────────────────

    [Fact]
    public void Paying_for_the_accepted_deal_fulfils_the_dicari()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);
        BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock).Accept(bounty, Buyer, listing, null, clock);

        bounty.Fulfil();
        bounty.Fulfil();

        bounty.Status.ShouldBe(BountyStatus.Fulfilled);
    }

    [Fact]
    public void A_dicari_with_no_accepted_deal_cannot_be_fulfilled() =>
        CodeOf(() => Open(TestClock.AtEpoch()).Fulfil()).ShouldBe("bounty.no_deal");

    [Fact]
    public void A_deal_that_falls_through_reopens_the_dicari_while_it_still_has_time()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock, days: 14);
        var listing = Live(clock);
        var proposal = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock);
        proposal.Accept(bounty, Buyer, listing, null, clock);

        clock.Advance(TimeSpan.FromDays(3));
        bounty.ReleaseDeal(clock);
        proposal.Lapse("bounty.offer_expired");

        bounty.Status.ShouldBe(BountyStatus.Open);
        bounty.AcceptedProposalId.ShouldBeNull();
        bounty.AcceptsProposals(clock).ShouldBeTrue();
        proposal.Status.ShouldBe(ProposalStatus.Lapsed);
    }

    [Fact]
    public void A_deal_that_falls_through_after_the_deadline_expires_the_dicari()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock, days: 7);
        var listing = Live(clock);
        BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock).Accept(bounty, Buyer, listing, null, clock);

        clock.Advance(TimeSpan.FromDays(8));
        bounty.Expire(clock).ShouldBeFalse();
        bounty.ReleaseDeal(clock);

        bounty.Status.ShouldBe(BountyStatus.Expired);
    }

    // ─── the offer, the order and standing ──────────────────────────────────

    [Fact]
    public void A_bounty_offer_is_born_accepted_and_checks_out_once_like_any_accepted_offer()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);
        var offer = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(1_250_000), null, null, clock)
            .Accept(bounty, Buyer, listing, null, clock);

        offer.MarkCheckedOut(Buyer, "order-1", clock);

        offer.Status.ShouldBe(OfferStatus.CheckedOut);
        CodeOf(() => offer.MarkCheckedOut(Buyer, "order-2", clock)).ShouldBe("offer.already_used");
    }

    [Fact]
    public void A_bounty_offer_expires_after_the_forty_eight_hour_window_if_never_checked_out()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);
        var offer = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(900_000), null, null, clock)
            .Accept(bounty, Buyer, listing, null, clock);

        clock.Advance(Offer.CheckoutWindow);

        offer.Expire(clock).ShouldBeTrue();
        offer.Status.ShouldBe(OfferStatus.Expired);
    }

    [Fact]
    public void A_stored_offer_with_no_origin_reads_as_a_nego_offer()
    {
        var clock = TestClock.AtEpoch();
        var now = clock.UtcNow;

        var offer = Offer.Rehydrate(
            "o1", "L1", Buyer, Seller, Money.FromRupiah(900_000), null, Money.FromRupiah(1_400_000), null, null, null, null,
            OfferStatus.Accepted, now, now.AddHours(48), now, 1);

        offer.Origin.ShouldBe(OfferOrigin.Nego);
        offer.BountyId.ShouldBeNull();
        offer.ProposalId.ShouldBeNull();
    }

    [Fact]
    public void A_nego_offer_carries_no_bounty_reference_even_if_one_was_stored()
    {
        var clock = TestClock.AtEpoch();
        var now = clock.UtcNow;

        var offer = Offer.Rehydrate(
            "o1", "L1", Buyer, Seller, Money.FromRupiah(900_000), null, Money.FromRupiah(1_400_000), null, null, null, null,
            OfferStatus.Accepted, now, now.AddHours(48), now, 1, OfferOrigin.Nego, "B1", "P1");

        offer.BountyId.ShouldBeNull();
        offer.ProposalId.ShouldBeNull();
    }

    [Fact]
    public void A_bounty_order_records_its_source_and_bounty_and_keeps_its_own_order_number()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        var listing = Live(clock);
        var offer = BountyProposal.Create(bounty, listing, Seller, Money.FromRupiah(1_250_000), null, null, clock)
            .Accept(bounty, Buyer, listing, null, clock);

        var order = Order.Create(
            Buyer, Seller, [new OrderLine(listing.Id, listing.Title, null, offer.CurrentAmount, 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet", "Bandung", "Jawa Barat", "40111"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3), clock,
            OrderSource.BountyOffer, clock.UtcNow + Order.PaymentWindow, offer.Id, bounty.Id);

        order.Source.ShouldBe(OrderSource.BountyOffer);
        order.SourceReference.ShouldBe(offer.Id);
        order.BountyId.ShouldBe(bounty.Id);
        order.ItemsTotal.ShouldBe(Money.FromRupiah(1_250_000));
        order.OrderNumber.ShouldStartWith("KLK-");
        order.Id.ShouldNotBe(bounty.Id);
        order.Id.ShouldNotBe(offer.Id);
    }

    [Fact]
    public void An_order_that_is_not_a_bounty_order_never_carries_a_bounty_id()
    {
        var clock = TestClock.AtEpoch();

        var order = Order.Create(
            Buyer, Seller, [new OrderLine("L1", "x", null, Money.FromRupiah(500_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet", "Bandung", "Jawa Barat", "40111"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3), clock,
            OrderSource.AcceptedOffer, clock.UtcNow + Order.PaymentWindow, "o1", "B1");

        order.BountyId.ShouldBeNull();
    }
}
