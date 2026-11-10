using Koleksi.Application.Common;
using Koleksi.Domain.Bounties;

namespace Koleksi.Application.Abstractions;

/// <summary>Reads and writes Dicari (bounties) and the proposals made on them.</summary>
public interface IBountyRepository
{
    /// <summary>The Dicari with its stored status; <c>null</c> when it does not exist.</summary>
    Task<Bounty?> FindAsync(string bountyId, CancellationToken ct);

    /// <summary>Stores a new Dicari, failing if the id exists.</summary>
    Task AddAsync(Bounty bounty, CancellationToken ct);

    /// <summary>
    /// Saves a Dicari, conditional on the version that was read. Assigns everything the aggregate owns and leaves
    /// <see cref="Bounty.ProposalCount"/> alone, because that is maintained by an atomic add.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The Dicari changed since it was read.</exception>
    Task UpdateAsync(Bounty bounty, long expectedVersion, CancellationToken ct);

    /// <summary>
    /// Open Dicari of one category that are still on the board, newest first, at most <paramref name="limit"/>.
    /// A Query on the category's board partition; a Dicari that has left the board is absent from it.
    /// </summary>
    Task<IReadOnlyList<Bounty>> ListOpenAsync(string categoryId, int limit, CancellationToken ct);

    /// <summary>How many Dicari a category's board partition holds right now (a count query, no rows read).</summary>
    Task<int> CountOpenAsync(string categoryId, CancellationToken ct);

    /// <summary>A buyer's own Dicari in every status, newest first.</summary>
    Task<Page<Bounty>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct);

    /// <summary>
    /// Open Dicari whose clock has run out: their deadline, or the deadline of the deal in flight on them. Longest
    /// overdue first, read from the sparse due index across its shards, never a scan.
    /// </summary>
    Task<IReadOnlyList<Bounty>> ListDueAsync(DateTimeOffset asOf, int limit, CancellationToken ct);

    /// <summary>
    /// Stores a proposal, bumps the Dicari's proposal count and claims the seller's one slot, in one transaction.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The seller already has a live proposal on this Dicari.</exception>
    Task AddProposalAsync(BountyProposal proposal, CancellationToken ct);

    /// <summary>
    /// Saves a proposal, conditional on its version. A withdrawn or lapsed proposal also frees the seller's slot.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The proposal changed since it was read.</exception>
    Task UpdateProposalAsync(BountyProposal proposal, long expectedVersion, CancellationToken ct);

    Task<BountyProposal?> FindProposalAsync(string bountyId, string proposalId, CancellationToken ct);

    /// <summary>Every proposal on a Dicari, oldest first.</summary>
    Task<IReadOnlyList<BountyProposal>> ListProposalsAsync(string bountyId, CancellationToken ct);

    /// <summary>The seller's live proposal on a Dicari (the one that holds their slot), if any.</summary>
    Task<BountyProposal?> FindLiveProposalBySellerAsync(string bountyId, string sellerId, CancellationToken ct);

    /// <summary>A seller's proposals across every Dicari, newest first.</summary>
    Task<Page<BountyProposal>> ListProposalsForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct);
}
