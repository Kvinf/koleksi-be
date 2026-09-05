using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
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
    IPaymentGateway payments,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>How long a checkout's payment session stays valid, matching <see cref="Order.PaymentWindow"/>.</summary>
    private static readonly TimeSpan SessionLifetime = Order.PaymentWindow;

    public async Task<CheckoutCartResult> HandleAsync(CheckoutCartCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ListingIds.Count == 0)
        {
            throw DomainException.For("cart.checkout_empty", "Select at least one item to check out.");
        }

        var buyerId = currentUser.RequireUserId();

        var shipTo = new ShippingAddress(
            command.ShipTo.RecipientName, command.ShipTo.Phone, command.ShipTo.Street,
            command.ShipTo.City, command.ShipTo.Province, command.ShipTo.PostalCode);

        var linesBySeller = new Dictionary<string, List<OrderLine>>(StringComparer.Ordinal);
        foreach (var listingId in command.ListingIds.Distinct(StringComparer.Ordinal))
        {
            var listing = await listings.FindAsync(listingId, ct).ConfigureAwait(false);

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

            lines.Add(new OrderLine(
                listing.Id,
                listing.Title,
                listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null,
                listing.Price,
                Quantity: 1));
        }

        if (linesBySeller.Count == 0)
        {
            return CheckoutCartResult.NothingPurchasable;
        }

        // Couriers are validated for every seller before anything is persisted, so a
        // missing choice fails the whole checkout cleanly rather than leaving some sellers'
        // orders created and others not.
        var created = new List<Order>(linesBySeller.Count);
        foreach (var (sellerId, lines) in linesBySeller)
        {
            if (!command.CourierCodeBySellerId.TryGetValue(sellerId, out var courierCode)
                || FlatRateCouriers.Find(courierCode) is not { } courier)
            {
                throw DomainException.For("order.invalid_courier", "Choose a courier for every seller.");
            }

            created.Add(Order.Create(buyerId, sellerId, lines, shipTo, courier, clock));
        }

        await orders.AddManyAsync(created, ct).ConfigureAwait(false);

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
                    new PaymentRequest(order.OrderNumber, order.GrandTotal, command.BuyerEmail, clock.UtcNow + SessionLifetime),
                    ct).ConfigureAwait(false);

                results.Add(CheckoutCartOrderResult.Success(order, session));
            }
            catch (DomainException failure) when (failure.Code == "payment.not_configured")
            {
                results.Add(CheckoutCartOrderResult.PaymentNotAvailable(order));
            }
        }

        return new CheckoutCartResult(true, results);
    }
}

/// <param name="ListingIds">The cart items the buyer ticked. Duplicates and unresolvable ids are ignored.</param>
/// <param name="BuyerEmail">
/// Resolved by the caller from the bearer token, never trusted from the request body —
/// same reasoning as <see cref="CheckoutCommand.BuyerEmail"/>.
/// </param>
/// <param name="CourierCodeBySellerId">
/// The buyer's per-shop courier pick, keyed by the seller id each cart row carries.
/// </param>
public sealed record CheckoutCartCommand(
    IReadOnlyList<string> ListingIds,
    string BuyerEmail,
    ShippingAddressInput ShipTo,
    IReadOnlyDictionary<string, string> CourierCodeBySellerId);

/// <param name="Found">
/// <c>false</c> when none of the requested listings were still purchasable — every one had
/// sold, been archived, or belonged to the buyer themself.
/// </param>
public sealed record CheckoutCartResult(bool Found, IReadOnlyList<CheckoutCartOrderResult> Orders)
{
    public static CheckoutCartResult NothingPurchasable { get; } = new(false, []);
}

public sealed record CheckoutCartOrderResult(
    string SellerId,
    string OrderId,
    string OrderNumber,
    long GrandTotalRupiah,
    Uri? PaymentRedirectUrl,
    string? PaymentQrisPayload,
    bool PaymentUnavailable)
{
    public static CheckoutCartOrderResult Success(Order order, PaymentSession session) => new(
        order.SellerId, order.Id, order.OrderNumber, order.GrandTotal.Rupiah,
        session.RedirectUrl, session.QrisPayload, false);

    public static CheckoutCartOrderResult PaymentNotAvailable(Order order) => new(
        order.SellerId, order.Id, order.OrderNumber, order.GrandTotal.Rupiah, null, null, true);
}
