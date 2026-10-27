using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Auctions;
using Koleksi.Application.Features.Cart;
using Koleksi.Application.Features.Watchlist;
using Koleksi.Application.Features.Catalog;
using Koleksi.Application.Features.Listings;
using Koleksi.Application.Features.Messaging;
using Koleksi.Application.Features.Offers;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Reviews;
using Koleksi.Application.Features.Sellers;
using Koleksi.Application.Features.Standing;
using Koleksi.Application.Features.Sweeps;
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

        // Auction price updates go out through whichever IRealtimeBroadcaster the host registered.
        services.AddSingleton<IAuctionEventPublisher, RealtimeAuctionEventPublisher>();

        // Scoped: handlers depend on ICurrentUser, which is per-request. Registering
        // them as singletons would capture one request's identity and hand it to every
        // subsequent caller on the same Lambda execution environment.
        services.AddScoped<BrowseListingsHandler>();
        services.AddScoped<SuggestSearchHandler>();
        services.AddScoped<GetListingHandler>();
        services.AddScoped<PlaceBidHandler>();
        services.AddScoped<BuyoutHandler>();
        services.AddScoped<CreateListingHandler>();
        services.AddScoped<UpdateListingHandler>();
        services.AddScoped<ArchiveListingHandler>();
        services.AddScoped<PublishListingHandler>();

        services.AddScoped<MakeOfferHandler>();
        services.AddScoped<AcceptOfferHandler>();
        services.AddScoped<DeclineOfferHandler>();
        services.AddScoped<CounterOfferHandler>();
        services.AddScoped<WithdrawOfferHandler>();
        services.AddScoped<OfferViews>();
        services.AddScoped<OfferNotifier>();

        services.AddScoped<StartConversationHandler>();
        services.AddScoped<SendMessageHandler>();
        services.AddScoped<GetConversationHandler>();
        services.AddScoped<ListConversationsHandler>();
        services.AddScoped<MarkConversationReadHandler>();

        services.AddScoped<CheckoutHandler>();
        services.AddScoped<ConfirmOrderPaymentHandler>();
        services.AddScoped<MarkOrderShippedHandler>();
        services.AddScoped<ConfirmDeliveryHandler>();
        services.AddScoped<OpenOrderDisputeHandler>();
        services.AddScoped<Features.Cases.ListMyCasesHandler>();
        services.AddScoped<Features.Cases.ReplyToCaseHandler>();
        services.AddScoped<Features.Cases.ListMyRefundsHandler>();
        services.AddScoped<CancelOrderHandler>();

        services.AddScoped<AddToCartHandler>();
        services.AddScoped<RemoveFromCartHandler>();
        services.AddScoped<GetCartHandler>();
        services.AddScoped<CheckoutCartHandler>();

        // Singleton: it holds a small cache of account id -> public handle, and depends only on
        // singleton adapters. Never on ICurrentUser, which would make it per-request.
        services.AddSingleton<PublicIdentityResolver>();
        services.AddScoped<GetSellerStorefrontHandler>();
        services.AddScoped<ListSellerListingsHandler>();
        services.AddScoped<ListSellerReviewsHandler>();
        services.AddScoped<GetOwnPublicProfileHandler>();
        services.AddScoped<UpdateOwnPublicProfileHandler>();

        services.AddScoped<SubmitReviewHandler>();
        services.AddScoped<GetOrderReviewHandler>();

        services.AddScoped<SaveListingHandler>();
        services.AddScoped<UnsaveListingHandler>();
        services.AddScoped<GetWatchlistHandler>();
        services.AddScoped<GetWatchedIdsHandler>();

        // The scheduled sweep. None of these read ICurrentUser: there is no caller, only a clock.
        services.AddScoped<AuctionCloseAnnouncer>();
        services.AddScoped<CloseDueAuctionsHandler>();
        services.AddSingleton<BanStatusCache>();
        services.AddScoped<AccountStandingService>();
        services.AddScoped<CancelUnpaidOrdersHandler>();
        services.AddScoped<ReleaseDueEscrowHandler>();
        services.AddScoped<TrackShipmentsHandler>();
        services.AddScoped<SendPendingPayoutsHandler>();
        services.AddScoped<ExpireAuctionPaymentsHandler>();
        services.AddScoped<EscrowReleaseService>();
        services.AddScoped<PayoutSender>();
        services.AddScoped<ApplyDeliveryNotificationHandler>();
        services.AddScoped<SweepOrchestrator>();

        return services;
    }

    /// <summary>
    /// The admin site's use cases, and only those. A separate method from <see cref="AddApplication"/> on
    /// purpose: the admin API must not resolve (or even contain registrations for) the marketplace's own
    /// handlers, which need repositories and ports it does not wire.
    /// </summary>
    public static IServiceCollection AddAdminApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IClock>(SystemClock.Instance);

        // Scoped: they read ICurrentStaff, which is per-request.
        services.AddScoped<Features.Admin.StartStaffSessionHandler>();
        services.AddScoped<Features.Admin.ListAuditHandler>();

        // Milestone 1: disputes and holidays.
        services.AddScoped<Features.Admin.GetOverviewHandler>();
        services.AddScoped<Features.Admin.ListDisputesHandler>();
        services.AddScoped<Features.Admin.GetDisputeHandler>();
        services.AddScoped<Features.Admin.AddDisputeNoteHandler>();
        services.AddScoped<Features.Admin.ResolveDisputeHandler>();
        services.AddScoped<Features.Admin.SettleRemainderHandler>();
        services.AddScoped<Features.Admin.ListHolidaysHandler>();
        services.AddScoped<Features.Admin.AddHolidayHandler>();
        services.AddScoped<Features.Admin.RemoveHolidayHandler>();

        return services;
    }
}
