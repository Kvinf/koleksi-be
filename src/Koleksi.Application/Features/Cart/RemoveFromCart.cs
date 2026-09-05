using Koleksi.Application.Abstractions;

namespace Koleksi.Application.Features.Cart;

/// <summary>Removes one listing from the authenticated caller's cart.</summary>
/// <remarks>
/// Deleting a row that is not there is already a no-op at the storage layer, so there is
/// nothing here for a "was it even in the cart" check to guard.
/// </remarks>
public sealed class RemoveFromCartHandler(ICartRepository cart, ICurrentUser currentUser)
{
    public async Task HandleAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var buyerId = currentUser.RequireUserId();
        await cart.RemoveItemAsync(buyerId, listingId, ct).ConfigureAwait(false);
    }
}
