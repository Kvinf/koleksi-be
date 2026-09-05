using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IOfferRepository"/> that honours the version condition.</summary>
public sealed class FakeOfferRepository : FakeAdapter, IOfferRepository
{
    private readonly Dictionary<string, Offer> _offers = new(StringComparer.Ordinal);

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

        if (!_offers.TryAdd(offer.Id, Copy(offer)))
        {
            throw new ConcurrencyConflictException($"Offer {offer.Id} already exists.");
        }

        return Task.CompletedTask;
    }

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

    public Task<Offer?> FindActiveAsync(string listingId, string buyerId, CancellationToken ct)
    {
        ThrowIfFailing();
        var active = _offers.Values.FirstOrDefault(o =>
            o.ListingId == listingId && o.BuyerId == buyerId
            && o.Status is OfferStatus.Pending or OfferStatus.Countered);
        return Task.FromResult(active is null ? null : Copy(active));
    }

    private static Offer Copy(Offer offer) => Offer.Rehydrate(
        offer.Id, offer.ListingId, offer.BuyerId, offer.SellerId, offer.Amount, offer.AskingPrice,
        offer.Message, offer.Status, offer.CreatedAt, offer.ExpiresAt, offer.RespondedAt, offer.Version);
}
