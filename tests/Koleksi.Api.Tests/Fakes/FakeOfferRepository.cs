using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IOfferRepository"/> that honours the version condition.</summary>
public sealed class FakeOfferRepository : FakeAdapter, IOfferRepository
{
    private readonly Dictionary<string, Offer> _offers = new(StringComparer.Ordinal);

    private readonly object _gate = new();

    private TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _holdUntilCallers;

    private int _held;

    public void Seed(Offer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        _offers[offer.Id] = offer;
    }

    public Task<Offer?> FindAsync(string offerId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(_offers.TryGetValue(offerId, out var offer) ? Copy(offer) : null);
    }

    public Task AddAsync(Offer offer, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(offer);

        lock (_gate)
        {
            // The real table keeps one "active offer" pointer per buyer and listing, written in the
            // same transaction as the offer, so a second open offer loses the write.
            var hasOpenOffer = _offers.Values.Any(o =>
                o.ListingId == offer.ListingId && o.BuyerId == offer.BuyerId && IsOpen(o.Status));
            if (IsOpen(offer.Status) && hasOpenOffer)
            {
                throw new ConcurrencyConflictException(
                    $"Buyer {offer.BuyerId} already has an open offer on {offer.ListingId}.");
            }

            if (!_offers.TryAdd(offer.Id, Copy(offer)))
            {
                throw new ConcurrencyConflictException($"Offer {offer.Id} already exists.");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Makes the first <c>n</c> calls to <see cref="FindActiveAsync"/> wait for each other before any
    /// of them returns, so n parallel requests all read "no open offer" before any of them writes.
    /// </summary>
    /// <remarks>
    /// Without it a race test depends on timing; with it, the losers are certain to reach the
    /// write and lose there, which is the path under test. Later calls are not held.
    /// </remarks>
    public void HoldFindActiveUntilCallers(int callers)
    {
        lock (_gate)
        {
            _holdUntilCallers = callers;
            _held = 0;
            _allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static bool IsOpen(OfferStatus status) =>
        status is OfferStatus.Pending or OfferStatus.Countered or OfferStatus.Accepted or OfferStatus.CheckedOut;

    public Task UpdateAsync(Offer offer, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(offer);

        if (!_offers.TryGetValue(offer.Id, out var stored) || stored.Version != expectedVersion)
        {
            throw new ConcurrencyConflictException($"Offer {offer.Id} changed since it was read.");
        }

        _offers[offer.Id] = Copy(offer);
        return Task.CompletedTask;
    }

    public Task<Page<Offer>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        var items = _offers.Values.Where(o => o.SellerId == sellerId)
            .OrderByDescending(o => o.CreatedAt).Take(limit).Select(Copy).ToList();
        return Task.FromResult(new Page<Offer>(items, null, items.Count));
    }

    public Task<Page<Offer>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        var items = _offers.Values.Where(o => o.BuyerId == buyerId)
            .OrderByDescending(o => o.CreatedAt).Take(limit).Select(Copy).ToList();
        return Task.FromResult(new Page<Offer>(items, null, items.Count));
    }

    public async Task<Offer?> FindActiveAsync(string listingId, string buyerId, CancellationToken ct)
    {
        ThrowIfFailing();

        Offer? result;
        Task? release = null;
        lock (_gate)
        {
            var active = _offers.Values.FirstOrDefault(o =>
                o.ListingId == listingId && o.BuyerId == buyerId && IsOpen(o.Status));
            result = active is null ? null : Copy(active);

            if (_holdUntilCallers > 0 && _held < _holdUntilCallers)
            {
                _held++;
                if (_held == _holdUntilCallers)
                {
                    _allArrived.SetResult();
                }

                release = _allArrived.Task;
            }
        }

        if (release is not null)
        {
            await release.ConfigureAwait(false);
        }

        return result;
    }

    public Task<IReadOnlyList<Offer>> ListOpenForListingAsync(string listingId, CancellationToken ct)
    {
        ThrowIfFailing();
        IReadOnlyList<Offer> open = _offers.Values
            .Where(o => o.ListingId == listingId
                && o.Status is OfferStatus.Pending or OfferStatus.Countered or OfferStatus.Accepted or OfferStatus.CheckedOut)
            .Select(Copy).ToList();
        return Task.FromResult(open);
    }

    private static Offer Copy(Offer offer) => Offer.Rehydrate(
        offer.Id, offer.ListingId, offer.BuyerId, offer.SellerId, offer.Amount, offer.CounterAmount, offer.AskingPrice,
        offer.Message, offer.BuyerUsername, offer.SellerUsername, offer.OrderId, offer.Status, offer.CreatedAt,
        offer.ExpiresAt, offer.RespondedAt, offer.Version);
}
