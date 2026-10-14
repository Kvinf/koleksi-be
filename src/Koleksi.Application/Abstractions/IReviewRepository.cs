using Koleksi.Application.Common;
using Koleksi.Domain.Reviews;

namespace Koleksi.Application.Abstractions;

/// <summary>Stores reviews and each seller's running rating.</summary>
public interface IReviewRepository
{
    /// <summary>
    /// Stores <paramref name="review"/> and adds it to its seller's rating counters, in one
    /// transaction, only if the order has not been reviewed yet.
    /// </summary>
    /// <returns><c>false</c> when the order already has a review; nothing was written.</returns>
    /// <remarks>
    /// One transaction so the counters can never disagree with the reviews they summarise, and
    /// the "once per order" rule is a condition on the write rather than a check before it —
    /// two submissions racing cannot both pass a read.
    /// </remarks>
    Task<bool> TryAddAsync(Review review, CancellationToken ct);

    /// <summary>The review written for an order, if any.</summary>
    Task<Review?> FindByOrderAsync(string orderId, CancellationToken ct);

    /// <summary>A seller's reviews, newest first.</summary>
    Task<Page<Review>> ListBySellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct);

    /// <summary>A seller's rating counters; <see cref="SellerRatingStats.Empty"/> when they have none.</summary>
    Task<SellerRatingStats> GetStatsAsync(string sellerId, CancellationToken ct);
}
