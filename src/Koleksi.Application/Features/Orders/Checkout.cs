using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;

namespace Koleksi.Application.Features.Orders;

/// <summary>Where the price and the seller for a checkout come from.</summary>
public enum CheckoutSource
{
    /// <summary>The listing's own asking price. Not offered for a live or scheduled auction.</summary>
    FixedPrice,

    /// <summary>The agreed price of an offer the seller has accepted.</summary>
    AcceptedOffer,

    /// <summary>An auction the caller won — by outbidding everyone, or by buyout.</summary>
    AuctionWin,
}

/// <summary>
/// Builds and persists an order for the authenticated caller, then starts a payment
/// session for it.
/// </summary>
/// <remarks>
/// <para>
/// The order is written before the payment session is requested, not after. A session
/// that never gets created still leaves a real, honest <c>AwaitingPayment</c> order behind
/// — one <see cref="Order.HasPaymentExpired"/> already knows how to describe, rather than
/// no record at all of what the caller tried to buy.
/// </para>
/// <para>
/// <see cref="IPaymentGateway.CreateSessionAsync"/> is a stub in this environment — see its
/// own remarks — so <see cref="CheckoutResult.PaymentUnavailable"/> is the normal outcome
/// here today, not a bug. It is caught rather than left to propagate so the caller still
/// learns the order id: "we could not start payment for order KLK-XXXXXX" is a materially
/// better answer than losing the order number in a thrown exception.
/// </para>
/// </remarks>
public sealed class CheckoutHandler(
    IListingRepository listings,
    IOfferRepository offers,
    IAuctionRepository auctions,
    IOrderRepository orders,
    IPaymentGateway payments,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>How long a checkout's payment session stays valid, matching <see cref="Order.PaymentWindow"/>.</summary>
    private static readonly TimeSpan SessionLifetime = Order.PaymentWindow;

    public async Task<CheckoutResult> HandleAsync(CheckoutCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var buyerId = currentUser.RequireUserId();

        var courier = FlatRateCouriers.Find(command.CourierCode)
            ?? throw DomainException.For("order.invalid_courier", "Choose a courier from the list.");

        var (sellerId, line) = await ResolveAsync(command, buyerId, ct).ConfigureAwait(false);
        if (line is null)
        {
            return CheckoutResult.NotFound;
        }

        var shipTo = new ShippingAddress(
            command.ShipTo.RecipientName, command.ShipTo.Phone, command.ShipTo.Street,
            command.ShipTo.City, command.ShipTo.Province, command.ShipTo.PostalCode);

        var order = Order.Create(buyerId, sellerId, [line], shipTo, courier, clock);
        await orders.AddManyAsync([order], ct).ConfigureAwait(false);

        try
        {
            var session = await payments.CreateSessionAsync(
                new PaymentRequest(order.OrderNumber, order.GrandTotal, command.BuyerEmail, clock.UtcNow + SessionLifetime),
                ct).ConfigureAwait(false);

            return CheckoutResult.Success(order, session);
        }
        catch (DomainException failure) when (failure.Code == "payment.not_configured")
        {
            return CheckoutResult.PaymentNotAvailable(order);
        }
    }

    private async Task<(string SellerId, OrderLine? Line)> ResolveAsync(
        CheckoutCommand command, string buyerId, CancellationToken ct)
    {
        switch (command.Source)
        {
            case CheckoutSource.FixedPrice:
            {
                var listing = await listings.FindAsync(command.ReferenceId, ct).ConfigureAwait(false);
                if (listing is null || listing.Status != ListingStatus.Active || listing.Format == ListingFormat.Auction)
                {
                    return (string.Empty, null);
                }

                return (listing.SellerId, LineFor(listing, listing.Price));
            }

            case CheckoutSource.AcceptedOffer:
            {
                var offer = await offers.FindAsync(command.ReferenceId, ct).ConfigureAwait(false);
                if (offer is null || offer.Status != OfferStatus.Accepted
                    || !string.Equals(offer.BuyerId, buyerId, StringComparison.Ordinal))
                {
                    return (string.Empty, null);
                }

                var listing = await listings.FindAsync(offer.ListingId, ct).ConfigureAwait(false);
                if (listing is null)
                {
                    return (string.Empty, null);
                }

                return (offer.SellerId, LineFor(listing, offer.Amount));
            }

            case CheckoutSource.AuctionWin:
            {
                var auction = await auctions.FindAsync(command.ReferenceId, ct).ConfigureAwait(false);
                if (auction is null || auction.Status != AuctionStatus.Ended
                    || !string.Equals(auction.LeadingBidderId, buyerId, StringComparison.Ordinal))
                {
                    return (string.Empty, null);
                }

                var listing = await listings.FindAsync(auction.ListingId, ct).ConfigureAwait(false);
                if (listing is null)
                {
                    return (string.Empty, null);
                }

                return (auction.SellerId, LineFor(listing, auction.CurrentPrice));
            }

            default:
                return (string.Empty, null);
        }
    }

    private static OrderLine LineFor(Listing listing, Money unitPrice) =>
        new(listing.Id, listing.Title, listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null, unitPrice, Quantity: 1);
}

/// <param name="Source">Which price and seller this checkout resolves from.</param>
/// <param name="ReferenceId">
/// A listing id for <see cref="CheckoutSource.FixedPrice"/> and
/// <see cref="CheckoutSource.AuctionWin"/>; an offer id for <see cref="CheckoutSource.AcceptedOffer"/>.
/// </param>
/// <param name="BuyerEmail">
/// Resolved by the caller from the bearer token (<c>IIdentityService.GetEmailAsync</c>),
/// never trusted from the request body — the payment gateway needs somewhere to send a
/// receipt, and that has to be the address the account actually owns.
/// </param>
public sealed record CheckoutCommand(
    CheckoutSource Source,
    string ReferenceId,
    string BuyerEmail,
    ShippingAddressInput ShipTo,
    string CourierCode);

public sealed record ShippingAddressInput(
    string RecipientName, string Phone, string Street, string City, string Province, string PostalCode);

/// <param name="Found"><c>false</c> when the referenced listing, offer, or auction is not checkoutable.</param>
/// <param name="OrderId">Present whenever an order was actually created.</param>
/// <param name="OrderNumber">The short, human-facing reference — see <c>Order.BuildOrderNumber</c>.</param>
/// <param name="GrandTotalRupiah">What the buyer owes, items plus shipping.</param>
/// <param name="PaymentRedirectUrl">Where to send the buyer to pay, once a session exists.</param>
/// <param name="PaymentQrisPayload">A QRIS payload to render inline, when the gateway offers one.</param>
/// <param name="PaymentUnavailable">
/// <c>true</c> when the order was created but the payment gateway could not start a
/// session — today, always, since <c>CreateSessionAsync</c> is not yet configured.
/// </param>
public sealed record CheckoutResult(
    bool Found,
    string? OrderId,
    string? OrderNumber,
    long? GrandTotalRupiah,
    Uri? PaymentRedirectUrl,
    string? PaymentQrisPayload,
    bool PaymentUnavailable)
{
    public static CheckoutResult NotFound { get; } = new(false, null, null, null, null, null, false);

    public static CheckoutResult Success(Order order, PaymentSession session) => new(
        true, order.Id, order.OrderNumber, order.GrandTotal.Rupiah, session.RedirectUrl, session.QrisPayload, false);

    public static CheckoutResult PaymentNotAvailable(Order order) =>
        new(true, order.Id, order.OrderNumber, order.GrandTotal.Rupiah, null, null, true);
}
