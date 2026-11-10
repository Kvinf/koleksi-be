using Koleksi.Application.Abstractions;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary><c>DynamoBountyRepository</c> against a real table.</summary>
public sealed class BountyRepositoryTests : DynamoTestBase
{
    private static Bounty NewBounty(string buyerId, string categoryId = "cards") => Bounty.Create(
        buyerId, null, "Dicari PSA 10 Charizard 1999", categoryId, new Dictionary<string, string>(), null, ["PSA 10"],
        Money.FromRupiah(2_000_000), "Bandung", [], 14, Fixtures.Clock);

    [DynamoLocalFact]
    public async Task A_dicari_survives_the_round_trip_and_is_on_its_boards_partition()
    {
        var bounty = NewBounty($"buyer-{Guid.NewGuid():N}", "cards");

        await Table.Bounties.AddAsync(bounty, Ct);

        var stored = await Table.Bounties.FindAsync(bounty.Id, Ct);
        stored.ShouldNotBeNull();
        stored.Title.ShouldBe(bounty.Title);
        stored.MaxBudget.Rupiah.ShouldBe(2_000_000);
        (await Table.Bounties.ListOpenAsync("cards", 200, Ct)).ShouldContain(b => b.Id == bounty.Id);
        (await Table.Bounties.CountOpenAsync("cards", Ct)).ShouldBeGreaterThan(0);
    }

    [DynamoLocalFact]
    public async Task A_stale_update_is_refused()
    {
        var bounty = NewBounty($"buyer-{Guid.NewGuid():N}");
        await Table.Bounties.AddAsync(bounty, Ct);
        var first = (await Table.Bounties.FindAsync(bounty.Id, Ct))!;
        var second = (await Table.Bounties.FindAsync(bounty.Id, Ct))!;

        var version = first.Version;
        first.Close(first.BuyerId, Fixtures.Clock);
        await Table.Bounties.UpdateAsync(first, version, Ct);

        var staleVersion = second.Version;
        second.Close(second.BuyerId, Fixtures.Clock);
        await Should.ThrowAsync<ConcurrencyConflictException>(() => Table.Bounties.UpdateAsync(second, staleVersion, Ct));
    }

    [DynamoLocalFact]
    public async Task A_closed_dicari_leaves_the_board_and_the_due_list()
    {
        var bounty = NewBounty($"buyer-{Guid.NewGuid():N}", "gunpla");
        await Table.Bounties.AddAsync(bounty, Ct);
        var version = bounty.Version;
        bounty.Close(bounty.BuyerId, Fixtures.Clock);
        await Table.Bounties.UpdateAsync(bounty, version, Ct);

        (await Table.Bounties.ListOpenAsync("gunpla", 200, Ct)).ShouldNotContain(b => b.Id == bounty.Id);
        (await Table.Bounties.ListDueAsync(bounty.ExpiresAt.AddDays(1), 200, Ct)).ShouldNotContain(b => b.Id == bounty.Id);
    }

    [DynamoLocalFact]
    public async Task An_open_dicari_past_its_deadline_is_found_by_the_due_list()
    {
        var bounty = NewBounty($"buyer-{Guid.NewGuid():N}");
        await Table.Bounties.AddAsync(bounty, Ct);

        (await Table.Bounties.ListDueAsync(bounty.ExpiresAt.AddMinutes(1), 200, Ct)).ShouldContain(b => b.Id == bounty.Id);
        (await Table.Bounties.ListDueAsync(bounty.ExpiresAt.AddMinutes(-1), 200, Ct)).ShouldNotContain(b => b.Id == bounty.Id);
    }

    [DynamoLocalFact]
    public async Task Adding_a_proposal_counts_it_and_holds_the_sellers_one_slot()
    {
        var bounty = NewBounty($"buyer-{Guid.NewGuid():N}");
        await Table.Bounties.AddAsync(bounty, Ct);
        var seller = $"seller-{Guid.NewGuid():N}";

        var first = Proposal(bounty, seller);
        await Table.Bounties.AddProposalAsync(first, Ct);
        await Should.ThrowAsync<ConcurrencyConflictException>(() => Table.Bounties.AddProposalAsync(Proposal(bounty, seller), Ct));

        (await Table.Bounties.FindAsync(bounty.Id, Ct))!.ProposalCount.ShouldBe(1);
        (await Table.Bounties.FindLiveProposalBySellerAsync(bounty.Id, seller, Ct))!.Id.ShouldBe(first.Id);
        (await Table.Bounties.ListProposalsAsync(bounty.Id, Ct)).Count.ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task A_withdrawn_proposal_frees_the_sellers_slot_and_an_update_keeps_the_count()
    {
        var bounty = NewBounty($"buyer-{Guid.NewGuid():N}");
        await Table.Bounties.AddAsync(bounty, Ct);
        var seller = $"seller-{Guid.NewGuid():N}";
        var proposal = Proposal(bounty, seller);
        await Table.Bounties.AddProposalAsync(proposal, Ct);

        var version = proposal.Version;
        proposal.Withdraw(seller);
        await Table.Bounties.UpdateProposalAsync(proposal, version, Ct);

        (await Table.Bounties.FindLiveProposalBySellerAsync(bounty.Id, seller, Ct)).ShouldBeNull();
        await Table.Bounties.AddProposalAsync(Proposal(bounty, seller), Ct);

        // A whole-item save of the Dicari must not wipe the count the proposal transactions maintain.
        var reread = (await Table.Bounties.FindAsync(bounty.Id, Ct))!;
        var bountyVersion = reread.Version;
        reread.Close(reread.BuyerId, Fixtures.Clock);
        await Table.Bounties.UpdateAsync(reread, bountyVersion, Ct);
        (await Table.Bounties.FindAsync(bounty.Id, Ct))!.ProposalCount.ShouldBe(2);
    }

    [DynamoLocalFact]
    public async Task A_buyers_dicari_are_listed_under_the_buyer_alone()
    {
        var buyer = $"buyer-{Guid.NewGuid():N}";
        var older = NewBounty(buyer);
        await Table.Bounties.AddAsync(older, Ct);
        var newer = NewBounty(buyer);
        await Table.Bounties.AddAsync(newer, Ct);

        var mine = await Table.Bounties.ListForBuyerAsync(buyer, null, 20, Ct);

        mine.Items.Select(b => b.Id).ShouldBe([newer.Id, older.Id], ignoreOrder: true);
    }

    private static BountyProposal Proposal(Bounty bounty, string sellerId)
    {
        var listing = Fixtures.ActiveListing();
        return BountyProposal.Rehydrate(
            EntityId.New(Fixtures.Clock.UtcNow), bounty.Id, sellerId, null, listing.Id, Money.FromRupiah(1_900_000),
            Money.FromRupiah(2_500_000), null, Fixtures.Clock.UtcNow, ProposalStatus.Pending, null, null, 0);
    }
}
