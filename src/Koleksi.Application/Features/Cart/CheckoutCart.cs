using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Standing;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Application.Features.Cart;

/// <summary>
/// Checks out every selected cart item in one go: one order per seller, each with its own
/// courier choice, sharing the one shipping address the buyer entered.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="CheckoutHandler"/>'s shape — resolve, build, persist, start a
/// payment session — but for several listings across several sellers instead of one
/// reference. <see cref="IOrderRepository.AddManyAsync"/> already exists for exactly this:
/// "a checkout containing items from three shops produces three orders", per
/// <see cref="Order"/>'s own remarks.
/// </para>
/// <para>
/// <b>Each resulting order still gets its own payment session</b>, not one shared across
/// all of them. <see cref="PaymentRequest.ReferenceId"/>'s doc comment already anticipates
/// a session covering "every order in one checkout", but <c>ConfirmOrderPaymentHandler</c>
/// resolves a webhook's reference to exactly one order by number — extending that to a
/// reference that fans out to several orders is a change to the payment webhook's
/// contract, the most security-sensitive path in the product, and is out of scope here.
/// A buyer checking out from three shops sees three payment steps today, the same as if
/// they had checked each shop out separately.
/// </para>
/// </remarks>
public sealed class CheckoutCartHandler(
    ICartRepository cart,
    IListingRepository listings,
    IOrderRepository orders,
    IListingHoldStore holds,
    IPaymentGateway payments,
    AccountStandingService standing,
    PublicIdentityResolver identities,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Most items one checkout may name. Bounds the per-request reads and the holds taken.</summary>
    public const int MaxItems = 20;

    public async Task<CheckoutCartResult> HandleAsync(CheckoutCartCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ListingIds.Count == 0)
        {
            throw DomainException.For("cart.checkout_empty", "Select at least one item to check out.");
        }

        if (command.ListingIds.Count > MaxItems)
        {
            throw DomainException.For("cart.too_many_items", $"A checkout may include at most {MaxItems} items.");
        }

        var buyerId = currentUser.RequireUserId();

        var shipTo = new ShippingAddress(
            command.ShipTo.RecipientName, command.ShipTo.Phone, command.ShipTo.Street,
            command.ShipTo.City, command.ShipTo.Province, command.ShipTo.PostalCode);

        var linesBySeller = new Dictionary<string, List<OrderLine>>(StringComparer.Ordinal);

        // How many of each the buyer put in the cart (the server's own record, not the request's), and how
        // many units each listing has: the numbers every line is reserved against.
        var quantityByListing = (await cart.ListAsync(buyerId, ct).ConfigureAwait(false))
            .ToDictionary(item => item.ListingId, item => item.Quantity, StringComparer.Ordinal);
        var stockByListing = new Dictionary<string, int>(StringComparer.Ordinal);

        // Every seller behind a requested listing that exists, purchasable or not: the shops the
        // buyer could legitimately have named a courier for, even if their item has since sold.
        var requestedSellers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listingId in command.ListingIds.Distinct(StringComparer.Ordinal))
        {
            var listing = await listings.FindAsync(listingId, ct).ConfigureAwait(false);

            if (listing is not null)
            {
                requestedSellers.Add(listing.SellerId);
            }

            // Dropped rather than failing the whole checkout: a listing that sold or was
            // archived between the cart screen loading and Checkout being pressed should
            // not strand every other seller's items in the same cart.
            if (listing is null || listing.Status != ListingStatus.Active
                || listing.Format == ListingFormat.Auction
                || string.Equals(listing.SellerId, buyerId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!linesBySeller.TryGetValue(listing.SellerId, out var lines))
            {
                lines = [];
                linesBySeller[listing.SellerId] = lines;
            }

            var quantity = quantityByListing.TryGetValue(listing.Id, out var wanted) ? wanted : 1;
            if (quantity < 1 || quantity > Math.Min(listing.Stock, IListingHoldStore.MaxQuantityPerOrder))
            {
                throw DomainException.For(
                    "order.invalid_quantity",
                    $"Choose between 1 and {Math.Min(listing.Stock, IListingHoldStore.MaxQuantityPerOrder)} units of '{listing.Title}'.");
            }

            stockByListing[listing.Id] = listing.Stock;
            lines.Add(new OrderLine(
                listing.Id,
                listing.Title,
                listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null,
                listing.Price,
                quantity));
        }

        if (linesBySeller.Count == 0)
        {
            return CheckoutCartResult.NothingPurchasable;
        }

        // The buyer names a shop by its public handle; the account id behind it is never on the
        // wire. Handles are mapped back to sellers here by resolving the sellers of this checkout's
        // own listings, so a handle can only ever select one of the shops actually being bought from.
        var handles = await identities.ResolveManyAsync(requestedSellers, ct).ConfigureAwait(false);
        var sellerByHandle = handles.ToDictionary(
            pair => pair.Value.Handle, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

        // Couriers are validated for every seller before anything is persisted, so a
        // missing choice fails the whole checkout cleanly rather than leaving some sellers'
        // orders created and others not.
        var courierBySeller = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (handle, courierCode) in command.CourierCodeByShop)
        {
            if (!sellerByHandle.TryGetValue(handle, out var shopSellerId))
            {
                throw DomainException.For("order.invalid_courier", "Choose a courier for the shops in this checkout.");
            }

            courierBySeller[shopSellerId] = courierCode;
        }

        var created = new List<Order>(linesBySeller.Count);
        foreach (var (sellerId, lines) in linesBySeller)
        {
            // A banned seller cannot be bought from, whatever browse still shows.
            await standing.EnsureNotBannedAsync(sellerId, ct).ConfigureAwait(false);

            if (!courierBySeller.TryGetValue(sellerId, out var courierCode)
                || string.IsNullOrWhiteSpace(courierCode)
                || FlatRateCouriers.Find(courierCode) is not { } courier)
            {
                throw DomainException.For("order.invalid_courier", "Choose a courier for every seller.");
            }

            created.Add(Order.Create(buyerId, sellerId, lines, shipTo, courier, clock));
        }

        await PendingOrderLimit.EnsureRoomAsync(orders, clock, buyerId, created, ct).ConfigureAwait(false);

        // Every item is held for this buyer first; if any is being bought by someone else the whole
        // checkout is refused and nothing stays reserved. See IListingHoldStore.
        var replaced = await OrderHolds.AcquireAsync(holds, buyerId, created, stockByListing, ct).ConfigureAwait(false);
        try
        {
            await orders.AddManyAsync(created, ct).ConfigureAwait(false);
        }
        catch
        {
            await OrderHolds.ReleaseAsync(holds, buyerId, created, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await OrderHolds.CancelReplacedAsync(orders, clock, buyerId, replaced, ct).ConfigureAwait(false);

        // The purchased items now belong to real orders and no longer belong in the cart —
        // whether or not a payment session ends up available for them below.
        foreach (var order in created)
        {
            foreach (var line in order.Lines)
            {
                await cart.RemoveItemAsync(buyerId, line.ListingId, ct).ConfigureAwait(false);
            }
        }

        var results = new List<CheckoutCartOrderResult>(created.Count);
        foreach (var order in created)
        {
            try
            {
                var session = await payments.CreateSessionAsync(
                    new PaymentRequest(order.OrderNumber, order.GrandTotal, command.BuyerEmail, order.PaymentDueAt),
                    ct).ConfigureAwait(false);

                results.Add(CheckoutCartOrderResult.Success(order, ShopOf(order), session));
            }
            catch (DomainException failure) when (failure.Code == "payment.not_configured")
            {
                results.Add(CheckoutCartOrderResult.PaymentNotAvailable(order, ShopOf(order)));
            }
        }

        return new CheckoutCartResult(true, results);

        string ShopOf(Order placed) => handles[placed.SellerId].Handle;
    }
}

/// <param name="ListingIds">The cart items the buyer ticked. Duplicates and unresolvable ids are ignored.</param>
/// <param name="BuyerEmail">
/// Resolved by the caller from the bearer token, never trusted from the request body —
/// same reasoning as <see cref="CheckoutCommand.BuyerEmail"/>.
/// </param>
/// <param name="CourierCodeByShop">
/// The buyer's per-shop courier pick, keyed by the shop's public handle (the one each cart row
/// carries as its shop handle). A handle that is not one of this checkout's shops is refused
/// (<c>order.invalid_courier</c>).
/// </param>
public sealed record CheckoutCartCommand(
    IReadOnlyList<string> ListingIds,
    string BuyerEmail,
    ShippingAddressInput ShipTo,
    IReadOnlyDictionary<string, string> CourierCodeByShop);

/// <param name="Found">
/// <c>false</c> when none of the requested listings were still purchasable — every one had
/// sold, been archived, or belonged to the buyer themself.
/// </param>
public sealed record CheckoutCartResult(bool Found, IReadOnlyList<CheckoutCartOrderResult> Orders)
{
    public static CheckoutCartResult NothingPurchasable { get; } = new(false, []);
}

/// <param name="ShopHandle">The seller's public handle; never the account id behind it.</param>
public sealed record CheckoutCartOrderResult(
    string ShopHandle,
    string OrderId,
    string OrderNumber,
    long GrandTotalRupiah,
    Uri? PaymentRedirectUrl,
    string? PaymentQrisPayload,
    bool PaymentUnavailable)
{
    public static CheckoutCartOrderResult Success(Order order, string shopHandle, PaymentSession session) => new(
        shopHandle, order.Id, order.OrderNumber, order.GrandTotal.Rupiah,
        session.RedirectUrl, session.QrisPayload, false);

    public static CheckoutCartOrderResult PaymentNotAvailable(Order order, string shopHandle) => new(
        shopHandle, order.Id, order.OrderNumber, order.GrandTotal.Rupiah, null, null, true);
}
