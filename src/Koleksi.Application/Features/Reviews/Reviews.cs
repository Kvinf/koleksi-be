using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Reviews;

namespace Koleksi.Application.Features.Reviews;

/// <summary>Writes the buyer's review of an order.</summary>
/// <remarks>
/// The reviewer is always the caller. An order that does not exist and one the caller did not buy
/// look the same from outside (<see cref="SubmitReviewResult.NotFound"/>), so an order id cannot be
/// probed through this door. The eligibility rules live on <see cref="Review.Create"/>.
/// </remarks>
public sealed class SubmitReviewHandler(
    IOrderRepository orders,
    IReviewRepository reviews,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<SubmitReviewResult> HandleAsync(SubmitReviewCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var userId = currentUser.RequireUserId();

        var order = await orders.FindAsync(command.OrderId, ct).ConfigureAwait(false);
        if (order is null || !string.Equals(order.BuyerId, userId, StringComparison.Ordinal))
        {
            // A seller reviewing their own sale lands here too, which is as good as the
            // self-review rule: they are not the buyer.
            return SubmitReviewResult.NotFound;
        }

        var review = Review.Create(order, userId, command.Rating, command.Text, command.IsAnonymous, clock);

        if (!await reviews.TryAddAsync(review, ct).ConfigureAwait(false))
        {
            throw DomainException.For("review.already_reviewed", "This order has already been reviewed.");
        }

        return new SubmitReviewResult(Found: true, review.Id);
    }
}

public sealed record SubmitReviewCommand(string OrderId, int Rating, string? Text, bool IsAnonymous);

public sealed record SubmitReviewResult(bool Found, string? ReviewId)
{
    public static SubmitReviewResult NotFound { get; } = new(false, null);
}

/// <summary>
/// What the order page and the review form need: may the caller review this order, and have they.
/// </summary>
public sealed class GetOrderReviewHandler(IOrderRepository orders, IReviewRepository reviews, ICurrentUser currentUser)
{
    public async Task<OrderReviewState?> HandleAsync(string orderId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();

        var order = await orders.FindAsync(orderId, ct).ConfigureAwait(false);
        if (order is null || !string.Equals(order.BuyerId, userId, StringComparison.Ordinal))
        {
            return null;
        }

        var existing = await reviews.FindByOrderAsync(order.Id, ct).ConfigureAwait(false);

        return new OrderReviewState(
            Eligible: existing is null && Review.CanReview(order, userId),
            Review: existing);
    }
}

/// <param name="Eligible">The order is in a reviewable state, the caller bought it, and it has no review yet.</param>
/// <param name="Review">The caller's existing review, if they wrote one.</param>
public sealed record OrderReviewState(bool Eligible, Review? Review);
