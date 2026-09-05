using Koleksi.Application.Features.Auctions;
using Koleksi.Application.Features.Cart;
using Koleksi.Application.Features.Catalog;
using Koleksi.Application.Features.Listings;
using Koleksi.Application.Features.Offers;
using Koleksi.Application.Features.Orders;
using Koleksi.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Koleksi.Application;

/// <summary>Registers the application layer's use-case handlers.</summary>
/// <remarks>
/// <para>
/// Handlers are plain classes resolved directly by the endpoints that need them. There
/// is no mediator: an in-process dispatcher would add a layer of indirection, lose
/// compile-time checking of which handler serves which endpoint, and make the call
/// graph unreadable in a stack trace — all to decouple two layers that ship together.
/// </para>
/// <para>
/// Registration is explicit rather than assembly-scanned. Scanning is convenient right
/// up to the moment it silently registers something nobody meant to expose.
/// </para>
/// </remarks>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IClock>(SystemClock.Instance);

        // Scoped: handlers depend on ICurrentUser, which is per-request. Registering
        // them as singletons would capture one request's identity and hand it to every
        // subsequent caller on the same Lambda execution environment.
        services.AddScoped<BrowseListingsHandler>();
        services.AddScoped<GetListingHandler>();
        services.AddScoped<PlaceBidHandler>();
        services.AddScoped<BuyoutHandler>();
        services.AddScoped<CreateListingHandler>();

        services.AddScoped<MakeOfferHandler>();
        services.AddScoped<AcceptOfferHandler>();
        services.AddScoped<DeclineOfferHandler>();
        services.AddScoped<CounterOfferHandler>();
        services.AddScoped<WithdrawOfferHandler>();

        services.AddScoped<CheckoutHandler>();
        services.AddScoped<ConfirmOrderPaymentHandler>();
        services.AddScoped<MarkOrderShippedHandler>();
        services.AddScoped<ConfirmDeliveryHandler>();
        services.AddScoped<OpenOrderDisputeHandler>();

        services.AddScoped<AddToCartHandler>();
        services.AddScoped<RemoveFromCartHandler>();
        services.AddScoped<GetCartHandler>();
        services.AddScoped<CheckoutCartHandler>();

        return services;
    }
}
