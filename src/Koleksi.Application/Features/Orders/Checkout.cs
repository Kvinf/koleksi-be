using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Offers;
using Koleksi.Application.Features.Standing;
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
    IListingHoldStore holds,
    IPaymentGateway payments,
    AccountStandingService standing,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<CheckoutResult> HandleAsync(CheckoutCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var buyerId = currentUser.RequireUserId();

        var courier = FlatRateCouriers.Find(command.CourierCode)
            ?? throw DomainException.For("order.invalid_courier", "Choose a courier from the list.");

        var resolved = await ResolveAsync(command, buyerId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return CheckoutResult.NotFound;
        }

        var (sellerId, line, spendingOffer, stock, source, dueAt, sourceReference) = resolved;

        // A banned seller cannot be bought from, whatever browse still shows.
        await standing.EnsureNotBannedAsync(sellerId, ct).ConfigureAwait(false);

        var shipTo = new ShippingAddress(
            command.ShipTo.RecipientName, command.ShipTo.Phone, command.ShipTo.Street,
            command.ShipTo.City, command.ShipTo.Province, command.ShipTo.PostalCode);

        // Created with its own deadline: fifteen minutes for a sale or an accepted offer, and for an
        // auction win whatever is left of the winner's thirty minutes from the close.
        var order = Order.Create(buyerId, sellerId, [line], shipTo, courier, clock, source, dueAt, sourceReference);

        await PendingOrderLimit.EnsureRoomAsync(orders, clock, buyerId, [order], ct).ConfigureAwait(false);

        // The units are reserved for this buyer before the order exists, so a second buyer's checkout
        // of the last unit is refused (out of stock, or held by someone who is paying) instead of racing
        // to pay for something already gone.
        var replaced = await OrderHolds.AcquireAsync(
            holds, buyerId, [order], new Dictionary<string, int>(StringComparer.Ordinal) { [line.ListingId] = stock }, ct)
            .ConfigureAwait(false);
        var offerSpent = false;
        try
        {
            // An accepted offer pays for exactly one order. It is claimed before the order is
            // written, by a write conditional on the version just read, so of two simultaneous
            // checkouts only one gets past here and the other is refused (offer.already_used).
            if (spendingOffer is not null)
            {
                await SpendOfferAsync(spendingOffer, buyerId, order.Id, ct).ConfigureAwait(false);
                offerSpent = true;
            }

            await orders.AddManyAsync([order], ct).ConfigureAwait(false);
        }
        catch
        {
            if (offerSpent && spendingOffer is not null)
            {
                await GiveOfferBackAsync(spendingOffer.Id).ConfigureAwait(false);
            }

            await OrderHolds.ReleaseAsync(holds, buyerId, [order], CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await OrderHolds.CancelReplacedAsync(orders, clock, buyerId, replaced, ct).ConfigureAwait(false);

        try
        {
            var session = await payments.CreateSessionAsync(
                new PaymentRequest(order.OrderNumber, order.GrandTotal, command.BuyerEmail, order.PaymentDueAt),
                ct).ConfigureAwait(false);

            return CheckoutResult.Success(order, session);
        }
        catch (DomainException failure) when (failure.Code == "payment.not_configured")
        {
            return CheckoutResult.PaymentNotAvailable(order);
        }
    }

    private async Task SpendOfferAsync(Offer offer, string buyerId, string orderId, CancellationToken ct)
    {
        var expectedVersion = offer.Version;
        offer.MarkCheckedOut(buyerId, orderId, clock);

        try
        {
            await offers.UpdateAsync(offer, expectedVersion, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw DomainException.For("offer.already_used", "This offer has already been used for an order.");
        }
    }

    /// <summary>
    /// Undoes <see cref="SpendOfferAsync"/> when the order it was claimed for could not be
    /// written, so a storage hiccup does not burn the buyer's accepted deal. Best effort: if
    /// even this fails, the offer reopens anyway once the (absent) order is seen to be dead.
    /// </summary>
    private async Task GiveOfferBackAsync(string offerId)
    {
        try
        {
            var current = await offers.FindAsync(offerId, CancellationToken.None).ConfigureAwait(false);
            if (current is { Status: OfferStatus.CheckedOut })
            {
                var version = current.Version;
                current.ReopenAfterUnpaidOrder(clock);
                await offers.UpdateAsync(current, version, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Swallowed on purpose: this runs while another failure is propagating, and
            // replacing that one with a compensation error would hide the real cause.
        }
    }

    private async Task<Resolved?> ResolveAsync(CheckoutCommand command, string buyerId, CancellationToken ct)
    {
        // Several units can only be bought as a plain sale: an accepted offer is for one item at one
        // agreed price, and an auction lot is one item.
        if (command.Quantity != 1 && command.Source != CheckoutSource.FixedPrice)
        {
            throw DomainException.For("order.invalid_quantity", "An offer or an auction is for a single item.");
        }

        switch (command.Source)
        {
            case CheckoutSource.FixedPrice:
            {
                var listing = await listings.FindAsync(command.ReferenceId, ct).ConfigureAwait(false);
                if (listing is null || listing.Status != ListingStatus.Active || listing.Format == ListingFormat.Auction)
                {
                    return null;
                }

                var most = Math.Min(listing.Stock, IListingHoldStore.MaxQuantityPerOrder);
                if (command.Quantity < 1 || command.Quantity > most)
                {
                    throw DomainException.For("order.invalid_quantity", $"Choose between 1 and {most} units.");
                }

                return new Resolved(
                    listing.SellerId, LineFor(listing, listing.Price, command.Quantity), null, listing.Stock,
                    OrderSource.FixedPrice, clock.UtcNow + Order.PaymentWindow, null);
            }

            case CheckoutSource.AcceptedOffer:
            {
                var offer = await offers.FindAsync(command.ReferenceId, ct).ConfigureAwait(false);
                if (offer is null || !string.Equals(offer.BuyerId, buyerId, StringComparison.Ordinal))
                {
                    return null;
                }

                // A lapsed deal is shown as lapsed, never checked out.
                offer = await OfferExpiry.ApplyAsync(offers, offer, clock, ct).ConfigureAwait(false);

                if (offer.Status == OfferStatus.CheckedOut)
                {
                    // Used once already. Unless that order was never paid and has died (a payment
                    // page the buyer closed), in which case the deal is theirs again.
                    if (!await OfferOrders.IsDeadAsync(orders, offer, clock, ct).ConfigureAwait(false))
                    {
                        throw DomainException.For("offer.already_used", "This offer has already been used for an order.");
                    }

                    var version = offer.Version;
                    offer.ReopenAfterUnpaidOrder(clock);
                    try
                    {
                        await offers.UpdateAsync(offer, version, ct).ConfigureAwait(false);
                    }
                    catch (ConcurrencyConflictException)
                    {
                        throw DomainException.For("offer.already_used", "This offer has already been used for an order.");
                    }
                }

                if (offer.Status == OfferStatus.Expired)
                {
                    throw DomainException.For("offer.expired", "This offer has expired.");
                }

                if (offer.Status != OfferStatus.Accepted)
                {
                    throw DomainException.For("offer.not_accepted", $"A {offer.Status} offer cannot be checked out.");
                }

                // Still for sale: an accepted offer on an item that has since sold or been
                // delisted is not something to take payment for.
                var listing = await listings.FindAsync(offer.ListingId, ct).ConfigureAwait(false);
                if (listing is null || listing.Status != ListingStatus.Active)
                {
                    return null;
                }

                return new Resolved(
                    offer.SellerId, LineFor(listing, offer.CurrentAmount, 1), offer, listing.Stock,
                    OrderSource.AcceptedOffer, clock.UtcNow + Order.PaymentWindow, offer.Id);
            }

            case CheckoutSource.AuctionWin:
            {
                var auction = await auctions.FindAsync(command.ReferenceId, ct).ConfigureAwait(false);
                if (auction is null || auction.Status != AuctionStatus.Ended
                    || !string.Equals(auction.LeadingBidderId, buyerId, StringComparison.Ordinal))
                {
                    return null;
                }

                var listing = await listings.FindAsync(auction.ListingId, ct).ConfigureAwait(false);
                if (listing is null || listing.Status != ListingStatus.Active)
                {
                    return null;
                }

                // The winner's thirty minutes run from the close, not from opening checkout.
                var due = auction.PaymentDueAt ?? clock.UtcNow;
                if (due <= clock.UtcNow)
                {
                    throw DomainException.For(
                        "auction.payment_window_closed",
                        "The 30 minutes to pay for this auction have run out.");
                }

                return new Resolved(
                    auction.SellerId, LineFor(listing, auction.CurrentPrice, 1), null, listing.Stock,
                    OrderSource.AuctionWin, due, listing.Id);
            }

            default:
                return null;
        }
    }

    private static OrderLine LineFor(Listing listing, Money unitPrice, int quantity) =>
        new(listing.Id, listing.Title, listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null, unitPrice, quantity);

    private sealed record Resolved(
        string SellerId, OrderLine Line, Offer? SpendingOffer, int Stock, OrderSource Source, DateTimeOffset DueAt,
        string? SourceReference);
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
    string CourierCode,
    int Quantity = 1);

public sealed record ShippingAddressInput(
    string RecipientName, string Phone, string Street, string City, string Province, string PostalCode)
{
    public const int MaxNameLength = 100;
    public const int MaxPhoneLength = 20;
    public const int MaxStreetLength = 240;
    public const int MaxCityLength = 100;
    public const int MaxPostalCodeLength = 10;

    /// <summary>
    /// Whether every field is present and within bounds. The address is copied onto the order as
    /// dispute evidence, so an unbounded one would be stored (and returned to both parties) as sent.
    /// </summary>
    public bool IsValid() =>
        Within(RecipientName, MaxNameLength)
        && Within(Phone, MaxPhoneLength)
        && Within(Street, MaxStreetLength)
        && Within(City, MaxCityLength)
        && Within(Province, MaxCityLength)
        && Within(PostalCode, MaxPostalCodeLength);

    private static bool Within(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max;
}

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
