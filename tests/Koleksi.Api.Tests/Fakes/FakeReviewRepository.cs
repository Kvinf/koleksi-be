using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Reviews;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IReviewRepository"/> with the same one-review-per-order rule as the real write.</summary>
public sealed class FakeReviewRepository : FakeAdapter, IReviewRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Review> _byOrder = new(StringComparer.Ordinal);

    public Task<bool> TryAddAsync(Review review, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(_byOrder.TryAdd(review.OrderId, review));
        }
    }

    public Task<Review?> FindByOrderAsync(string orderId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(_byOrder.GetValueOrDefault(orderId));
        }
    }

    public Task<Page<Review>> ListBySellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            // The cursor is the id of the last review already returned; ids are time-ordered.
            var items = _byOrder.Values
                .Where(r => r.SellerId == sellerId)
                .OrderByDescending(r => r.Id, StringComparer.Ordinal)
                .Where(r => cursor is null || string.CompareOrdinal(r.Id, cursor) < 0)
                .Take(limit + 1)
                .ToList();

            var more = items.Count > limit;
            var page = items.Take(limit).ToList();
            return Task.FromResult(new Page<Review>(page, more ? page[^1].Id : null, page.Count));
        }
    }

    public Task<SellerRatingStats> GetStatsAsync(string sellerId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var mine = _byOrder.Values.Where(r => r.SellerId == sellerId).ToList();
            var histogram = Enumerable.Range(1, 5).Select(star => (long)mine.Count(r => r.Rating == star)).ToList();
            return Task.FromResult(new SellerRatingStats(mine.Count, mine.Sum(r => (long)r.Rating), histogram));
        }
    }
}
