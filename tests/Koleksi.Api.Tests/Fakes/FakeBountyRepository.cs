using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Common;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// In-memory Dicari store. Returns copies, never the stored instance, and checks versions the way the table does, so
/// a handler that forgets to save, or saves over someone else's change, fails here as it would in DynamoDB.
/// </summary>
public sealed class FakeBountyRepository : FakeAdapter, IBountyRepository
{
    private readonly Dictionary<string, Bounty> _bounties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BountyProposal> _proposals = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Seed(Bounty bounty)
    {
        ArgumentNullException.ThrowIfNull(bounty);
        lock (_gate)
        {
            _bounties[bounty.Id] = Copy(bounty);
        }
    }

    public Task<Bounty?> FindAsync(string bountyId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(_bounties.TryGetValue(bountyId, out var bounty) ? Copy(bounty) : null);
        }
    }

    public Task AddAsync(Bounty bounty, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (!_bounties.TryAdd(bounty.Id, Copy(bounty)))
            {
                throw new ConcurrencyConflictException($"Dicari {bounty.Id} already exists.");
            }
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Bounty bounty, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (!_bounties.TryGetValue(bounty.Id, out var stored) || stored.Version != expectedVersion)
            {
                throw new ConcurrencyConflictException($"Dicari {bounty.Id} changed since it was read.");
            }

            // The proposal count is owned by the proposal transaction, as in the real table.
            _bounties[bounty.Id] = Copy(bounty, stored.ProposalCount);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Bounty>> ListOpenAsync(string categoryId, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<Bounty> items = [.. _bounties.Values
                .Where(b => b.CategoryId == categoryId && b.Status == BountyStatus.Open && b.AcceptedProposalId is null)
                .OrderByDescending(b => b.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(b => Copy(b))];
            return Task.FromResult(items);
        }
    }

    public Task<int> CountOpenAsync(string categoryId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(_bounties.Values.Count(b =>
                b.CategoryId == categoryId && b.Status == BountyStatus.Open && b.AcceptedProposalId is null));
        }
    }

    public Task<Page<Bounty>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<Bounty> items = [.. _bounties.Values
                .Where(b => b.BuyerId == buyerId)
                .OrderByDescending(b => b.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(b => Copy(b))];
            return Task.FromResult(new Page<Bounty>(items, null, items.Count));
        }
    }

    public Task<IReadOnlyList<Bounty>> ListDueAsync(DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<Bounty> items = [.. _bounties.Values
                .Where(b => b.Status == BountyStatus.Open && (b.DealDueAt ?? b.ExpiresAt) <= asOf)
                .OrderBy(b => b.DealDueAt ?? b.ExpiresAt)
                .Take(limit)
                .Select(b => Copy(b))];
            return Task.FromResult(items);
        }
    }

    public Task AddProposalAsync(BountyProposal proposal, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (!_bounties.TryGetValue(proposal.BountyId, out var bounty))
            {
                throw new ConcurrencyConflictException($"Dicari {proposal.BountyId} does not exist.");
            }

            var holdsSlot = _proposals.Values.Any(p =>
                p.BountyId == proposal.BountyId && p.SellerId == proposal.SellerId && IsLive(p.Status));
            if (holdsSlot || !_proposals.TryAdd(proposal.Id, Copy(proposal)))
            {
                throw new ConcurrencyConflictException(
                    $"Seller {proposal.SellerId} already has a proposal on {proposal.BountyId}.");
            }

            _bounties[bounty.Id] = Copy(bounty, bounty.ProposalCount + 1);
        }

        return Task.CompletedTask;
    }

    public Task UpdateProposalAsync(BountyProposal proposal, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (!_proposals.TryGetValue(proposal.Id, out var stored) || stored.Version != expectedVersion)
            {
                throw new ConcurrencyConflictException($"Proposal {proposal.Id} changed since it was read.");
            }

            _proposals[proposal.Id] = Copy(proposal);
        }

        return Task.CompletedTask;
    }

    public Task<BountyProposal?> FindProposalAsync(string bountyId, string proposalId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(
                _proposals.TryGetValue(proposalId, out var proposal) && proposal.BountyId == bountyId ? Copy(proposal) : null);
        }
    }

    public Task<IReadOnlyList<BountyProposal>> ListProposalsAsync(string bountyId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<BountyProposal> items = [.. _proposals.Values
                .Where(p => p.BountyId == bountyId)
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(Copy)];
            return Task.FromResult(items);
        }
    }

    public Task<BountyProposal?> FindLiveProposalBySellerAsync(string bountyId, string sellerId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var live = _proposals.Values.FirstOrDefault(p =>
                p.BountyId == bountyId && p.SellerId == sellerId && IsLive(p.Status));
            return Task.FromResult(live is null ? null : Copy(live));
        }
    }

    public Task<Page<BountyProposal>> ListProposalsForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<BountyProposal> items = [.. _proposals.Values
                .Where(p => p.SellerId == sellerId)
                .OrderByDescending(p => p.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(Copy)];
            return Task.FromResult(new Page<BountyProposal>(items, null, items.Count));
        }
    }

    /// <summary>Withdrawn and lapsed proposals give their slot back; the rest keep it, as in the table.</summary>
    private static bool IsLive(ProposalStatus status) => status is not (ProposalStatus.Withdrawn or ProposalStatus.Lapsed);

    private static Bounty Copy(Bounty b, int? proposalCount = null) => Bounty.Rehydrate(
        b.Id, b.BuyerId, b.BuyerUsername, b.Title, b.CategoryId, b.Facets, b.Description, b.Conditions, b.MaxBudget,
        b.ShipToCity, b.PhotoKeys, b.CreatedAt, b.ExpiresAt, b.Status, proposalCount ?? b.ProposalCount,
        b.AcceptedProposalId, b.RemovedReason, b.DealDueAt, b.Version);

    private static BountyProposal Copy(BountyProposal p) => BountyProposal.Rehydrate(
        p.Id, p.BountyId, p.SellerId, p.SellerUsername, p.ListingId, p.ProposedPrice, p.ListingPriceAtProposal, p.Note,
        p.CreatedAt, p.Status, p.OfferId, p.LapseReason, p.Version);
}
