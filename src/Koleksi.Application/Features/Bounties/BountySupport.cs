using Koleksi.Application.Abstractions;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Bounties;

/// <summary>
/// The push that tells a person's open pages a Dicari or one of its proposals has moved.
/// </summary>
/// <remarks>
/// A signal and nothing more, like <c>OfferNotifier</c>: the Dicari id, to one person's private topic. The page that
/// receives it re-reads through its ordinary, authenticated loader, so no price, name or note rides the socket. Best
/// effort: the change is already saved, and a missed push is repaired by the next page load.
/// </remarks>
public sealed partial class BountyNotifier(IRealtimeBroadcaster realtime, ILogger<BountyNotifier> logger)
{
    public const string BountyEvent = "bounty";

    public async Task NotifyAsync(string recipientId, string bountyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bountyId);

        try
        {
            await realtime
                .PublishAsync(RealtimeTopics.User(recipientId), BountyEvent, new { bountyId }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNotifyFailed(logger, bountyId, failure);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Dicari {BountyId} changed, but pushing the change to the other party failed.")]
    private static partial void LogNotifyFailed(ILogger logger, string bountyId, Exception exception);
}

/// <summary>
/// What happens to a Dicari as its deal and its clock move: a paid order fulfils it, a deal that falls through gives
/// it back, a passed deadline expires it. Shared by the order payment path and the sweep.
/// </summary>
public sealed partial class BountyDeals(
    IBountyRepository bounties,
    IOfferRepository offers,
    IOrderRepository orders,
    BountyNotifier notifier,
    IClock clock,
    ILogger<BountyDeals> logger)
{
    /// <summary>Why a proposal lapsed, as a stable code the screens turn into words.</summary>
    public static class Reasons
    {
        public const string ListingUnavailable = "bounty.listing_unavailable";
        public const string DealFellThrough = "bounty.offer_expired";
        public const string BountyEnded = "bounty.bounty_ended";
    }

    /// <summary>
    /// An order made from this Dicari's accepted offer was paid. Best effort and idempotent: the payment has already
    /// happened, and the sweep finds a Dicari this missed by looking at its deal again.
    /// </summary>
    public async Task FulfilAsync(string bountyId, CancellationToken ct)
    {
        try
        {
            var bounty = await bounties.FindAsync(bountyId, ct).ConfigureAwait(false);
            if (bounty is null || bounty.Status == BountyStatus.Fulfilled)
            {
                return;
            }

            var expectedVersion = bounty.Version;
            bounty.Fulfil();
            await bounties.UpdateAsync(bounty, expectedVersion, ct).ConfigureAwait(false);
            await LapsePendingAsync(bounty, Reasons.BountyEnded, ct).ConfigureAwait(false);
            await notifier.NotifyAsync(bounty.BuyerId, bounty.Id, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is DomainException or ConcurrencyConflictException)
        {
            LogFulfilSkipped(logger, bountyId, failure);
        }
    }

    /// <summary>
    /// Looks at a Dicari whose clock ran out and does what its state calls for. Returns <c>true</c> when it changed.
    /// </summary>
    /// <remarks>
    /// With no deal in flight it simply expires. With one, the deal is read as it really stands (the offer, and the
    /// order made from it): a paid order fulfils the Dicari, anything still alive is left, and everything else is a
    /// deal that fell through, so the proposal lapses and the Dicari opens again (or expires if its own deadline has
    /// passed). The order's own cancel sweep and standing rule have already dealt with an unpaid checkout.
    /// </remarks>
    public async Task<bool> ReconcileAsync(Bounty bounty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bounty);

        if (bounty.Status != BountyStatus.Open)
        {
            return false;
        }

        var expectedVersion = bounty.Version;

        if (!bounty.DealInFlight)
        {
            if (!bounty.Expire(clock))
            {
                return false;
            }

            await bounties.UpdateAsync(bounty, expectedVersion, ct).ConfigureAwait(false);
            await LapsePendingAsync(bounty, Reasons.BountyEnded, ct).ConfigureAwait(false);
            await notifier.NotifyAsync(bounty.BuyerId, bounty.Id, ct).ConfigureAwait(false);
            return true;
        }

        var proposal = await bounties.FindProposalAsync(bounty.Id, bounty.AcceptedProposalId!, ct).ConfigureAwait(false);
        var offer = proposal?.OfferId is { } offerId ? await offers.FindAsync(offerId, ct).ConfigureAwait(false) : null;
        var order = offer?.OrderId is { } orderId ? await orders.FindAsync(orderId, ct).ConfigureAwait(false) : null;

        if (order is { Source: OrderSource.BountyOffer } && order.EscrowState != EscrowState.AwaitingPayment
            && order.EscrowState != EscrowState.Cancelled)
        {
            bounty.Fulfil();
            await bounties.UpdateAsync(bounty, expectedVersion, ct).ConfigureAwait(false);
            await LapsePendingAsync(bounty, Reasons.BountyEnded, ct).ConfigureAwait(false);
            await notifier.NotifyAsync(bounty.BuyerId, bounty.Id, ct).ConfigureAwait(false);
            return true;
        }

        if (order is { EscrowState: EscrowState.AwaitingPayment } && !order.HasPaymentExpired(clock))
        {
            // The buyer is paying right now. Their order's own deadline decides, and the sweep looks again.
            return false;
        }

        await ReleaseDealAsync(bounty, proposal, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reads of a Dicari whose clock has run out settle it on the spot, so a person never sees "open" on something that
    /// ended. Best effort: the sweep does the same later, and a lost race simply returns what is stored.
    /// </summary>
    public async Task<Bounty> ApplyExpiryAsync(Bounty bounty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bounty);

        var due = bounty.DealDueAt ?? bounty.ExpiresAt;
        if (bounty.Status != BountyStatus.Open || due > clock.UtcNow)
        {
            return bounty;
        }

        try
        {
            await ReconcileAsync(bounty, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is DomainException or ConcurrencyConflictException)
        {
            LogFulfilSkipped(logger, bounty.Id, failure);
        }

        return await bounties.FindAsync(bounty.Id, ct).ConfigureAwait(false) ?? bounty;
    }

    /// <summary>
    /// The order made from a Dicari's offer was cancelled or died unpaid. A Dicari deal is never reopened for a second
    /// checkout, so the Dicari goes back to its buyer now instead of waiting out the deal's 49 hours. Best effort and
    /// idempotent; the sweep reaches the same state at the deadline.
    /// </summary>
    public async Task ReleaseAfterOrderDiedAsync(Order order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (order.Source != OrderSource.BountyOffer || order.BountyId is not { } bountyId)
        {
            return;
        }

        try
        {
            var bounty = await bounties.FindAsync(bountyId, ct).ConfigureAwait(false);
            if (bounty is not { Status: BountyStatus.Open, DealInFlight: true })
            {
                return;
            }

            var proposal = await bounties.FindProposalAsync(bounty.Id, bounty.AcceptedProposalId!, ct).ConfigureAwait(false);
            await ReleaseDealAsync(bounty, proposal, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is DomainException or ConcurrencyConflictException)
        {
            LogFulfilSkipped(logger, bountyId, failure);
        }
    }

    /// <summary>The accepted deal fell through: lapse its proposal and give the Dicari back.</summary>
    public async Task ReleaseDealAsync(Bounty bounty, BountyProposal? acceptedProposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bounty);

        var expectedVersion = bounty.Version;
        bounty.ReleaseDeal(clock);
        await bounties.UpdateAsync(bounty, expectedVersion, ct).ConfigureAwait(false);

        if (acceptedProposal is { Status: ProposalStatus.Accepted })
        {
            await SaveLapsedAsync(acceptedProposal, Reasons.DealFellThrough, ct).ConfigureAwait(false);
            await notifier.NotifyAsync(acceptedProposal.SellerId, bounty.Id, ct).ConfigureAwait(false);
        }

        if (bounty.Status != BountyStatus.Open)
        {
            await LapsePendingAsync(bounty, Reasons.BountyEnded, ct).ConfigureAwait(false);
        }

        await notifier.NotifyAsync(bounty.BuyerId, bounty.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Lapses every pending proposal on a Dicari that is no longer taking them. Best effort per proposal.</summary>
    public async Task LapsePendingAsync(Bounty bounty, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bounty);

        var proposals = await bounties.ListProposalsAsync(bounty.Id, ct).ConfigureAwait(false);
        foreach (var proposal in proposals.Where(p => p.Status == ProposalStatus.Pending))
        {
            if (await SaveLapsedAsync(proposal, reason, ct).ConfigureAwait(false))
            {
                await notifier.NotifyAsync(proposal.SellerId, bounty.Id, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Lapses a pending proposal whose listing can no longer back it. Returns <c>true</c> when it changed.</summary>
    public Task<bool> LapseBecauseListingGoneAsync(BountyProposal proposal, CancellationToken ct) =>
        SaveLapsedAsync(proposal, Reasons.ListingUnavailable, ct);

    private async Task<bool> SaveLapsedAsync(BountyProposal proposal, string reason, CancellationToken ct)
    {
        var expectedVersion = proposal.Version;
        proposal.Lapse(reason);
        if (proposal.Version == expectedVersion)
        {
            return false;
        }

        try
        {
            await bounties.UpdateProposalAsync(proposal, expectedVersion, ct).ConfigureAwait(false);
            return true;
        }
        catch (ConcurrencyConflictException failure)
        {
            // Someone else moved it first; theirs is the truth.
            LogLapseSkipped(logger, proposal.Id, failure);
            return false;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Dicari {BountyId} was not fulfilled from the payment path; the sweep will look at it again.")]
    private static partial void LogFulfilSkipped(ILogger logger, string bountyId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Proposal {ProposalId} was changed by someone else before it could be lapsed.")]
    private static partial void LogLapseSkipped(ILogger logger, string proposalId, Exception exception);
}

/// <summary>Whether a listing can still back a proposal, read for the proposal's seller.</summary>
internal static class BountyListings
{
    public static string? WhyGone(Bounty bounty, Listing? listing, BountyProposal proposal) =>
        BountyProposal.WhyListingDoesNotFit(bounty, listing, proposal.SellerId);
}
