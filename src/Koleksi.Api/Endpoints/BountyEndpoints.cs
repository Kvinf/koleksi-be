using Koleksi.Api.Security;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Bounties;
using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>Dicari (wanted posts): a buyer posts an item and a budget, sellers answer with a listing and a price.</summary>
/// <remarks>
/// <para>
/// Three kinds of caller meet here. The board, a Dicari's page and the suggestions are public reads
/// (<c>AllowAnonymous</c>, a decision: the board is what search engines and a logged-out visitor should see). Everything
/// that writes needs an identity, and who may do it is decided in the handler from <see cref="ICurrentUser"/>, never from
/// the route or body. A Dicari's detail is not output-cached because its content depends on who is looking.
/// </para>
/// <para>
/// Four ids appear and none stands in for another: the Dicari (<c>bountyId</c>), a seller's proposal on it
/// (<c>proposalId</c>), the accepted offer that exists once the buyer says yes (<c>offerId</c>), and the order the offer is
/// paid through (<c>orderId</c>).
/// </para>
/// </remarks>
internal static class BountyEndpoints
{
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapBountyEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/bounties").WithTags("Bounties");

        group.MapGet("", ListBoardAsync).AllowAnonymous().WithName("ListBounties");
        group.MapGet("/suggest", SuggestAsync).AllowAnonymous().WithName("SuggestBounties");
        group.MapGet("/{bountyId}", GetBountyAsync).AllowAnonymous().WithName("GetBounty");

        group.MapGet("/mine", ListMineAsync).WithName("ListMyBounties");
        group.MapGet("/matches", MatchesAsync).WithName("ListBountyMatches");
        group.MapGet("/proposals/mine", ListMyProposalsAsync).WithName("ListMyProposals");

        group.MapPost("", PostBountyAsync).WithName("PostBounty");
        group.MapPatch("/{bountyId}", EditBountyAsync).WithName("EditBounty");
        group.MapPost("/{bountyId}/close", CloseBountyAsync).WithName("CloseBounty");
        group.MapPost("/{bountyId}/report", ReportBountyAsync).WithName("ReportBounty");

        group.MapPost("/{bountyId}/proposals", MakeProposalAsync).WithName("MakeProposal");
        group.MapPost("/{bountyId}/proposals/{proposalId}/accept", AcceptProposalAsync).WithName("AcceptProposal");
        group.MapPost("/{bountyId}/proposals/{proposalId}/decline", DeclineProposalAsync).WithName("DeclineProposal");
        group.MapPost("/{bountyId}/proposals/{proposalId}/withdraw", WithdrawProposalAsync).WithName("WithdrawProposal");

        return app;
    }

    private static async Task<IResult> ListBoardAsync(
        BountyViews views, IMediaStorage media, string? category, string? sort, int? page, CancellationToken ct)
    {
        var board = await views.ListBoardAsync(category, ParseSort(sort), page ?? 1, ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            items = board.Page.Items.Select(card => ToCard(card, media)),
            page = board.Page.PageNumber,
            pageSize = board.Page.PageSize,
            totalCount = board.Page.TotalCount,
            totalPages = board.Page.TotalPages,
            categories = board.Categories.Select(c => new { c.CategoryId, c.CategorySlug, c.Count }),
        });
    }

    private static async Task<IResult> SuggestAsync(
        BountyViews views, IMediaStorage media, string? q, string? category, CancellationToken ct)
    {
        var (cards, total) = await views.SuggestAsync(q, category, ct).ConfigureAwait(false);
        return Results.Ok(new { items = cards.Select(card => ToCard(card, media)), total });
    }

    private static async Task<IResult> GetBountyAsync(string bountyId, BountyViews views, IMediaStorage media, CancellationToken ct)
    {
        var view = await views.GetAsync(bountyId, ct).ConfigureAwait(false);
        return view is null
            ? Results.NotFound(new { code = "bounty.not_found" })
            : Results.Ok(new
            {
                card = ToCard(view.Card, media),
                view.Description,
                view.Conditions,
                photos = view.PhotoKeys.Select(key => new { key, url = media.OriginalUrl(key) }),
                view.Role,
                view.BuyerMemberSince,
                view.BuyerContactVerified,
                proposals = view.Proposals.Select(p => ToProposal(p, media)),
                view.CanPropose,
                view.DealInFlight,
                view.AcceptedProposalId,
            });
    }

    private static async Task<IResult> ListMineAsync(BountyViews views, IMediaStorage media, string? cursor, CancellationToken ct)
    {
        var (rows, next) = await views.ListMineAsync(cursor, ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            items = rows.Select(r => new { card = ToCard(r.Card, media), r.DealInFlight }),
            nextCursor = next,
        });
    }

    private static async Task<IResult> MatchesAsync(BountyViews views, IMediaStorage media, CancellationToken ct)
    {
        var (cards, total) = await views.MatchesAsync(ct).ConfigureAwait(false);
        return Results.Ok(new { items = cards.Select(card => ToCard(card, media)), total });
    }

    private static async Task<IResult> ListMyProposalsAsync(BountyViews views, IMediaStorage media, string? cursor, CancellationToken ct)
    {
        var (rows, next) = await views.ListMyProposalsAsync(cursor, ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            items = rows.Select(r => new { proposal = ToProposal(r.Proposal, media), bounty = ToCard(r.Bounty, media) }),
            nextCursor = next,
        });
    }

    private static async Task<IResult> PostBountyAsync(
        PostBountyRequest request,
        PostBountyHandler handler,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var command = new PostBountyCommand(
            request.Title ?? string.Empty,
            request.CategoryId ?? string.Empty,
            request.Facets ?? new Dictionary<string, string>(),
            request.Description,
            request.Conditions ?? [],
            request.MaxBudgetRupiah,
            request.PhotoKeys ?? [],
            request.DurationDays);

        var created = await idempotency.RunOnceAsync(
            $"bounty.post:{user.RequireUserId()}",
            Read(http),
            IdempotencyRetention,
            () => handler.HandleAsync(command, ct),
            ct,
            InFlightPolicy.Reject,
            RequestFingerprint.Of(command.Title, command.CategoryId, command.MaxBudgetRupiah, command.DurationDays))
            .ConfigureAwait(false);

        return Results.Ok(new { bountyId = created.BountyId, code = created.Code });
    }

    private static async Task<IResult> EditBountyAsync(
        string bountyId, EditBountyRequest request, EditBountyHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var found = await handler.HandleAsync(
            new EditBountyCommand(
                bountyId, request.Title ?? string.Empty, request.Description, request.MaxBudgetRupiah,
                request.Conditions ?? [], request.PhotoKeys ?? [], request.CategoryId, request.Facets),
            ct).ConfigureAwait(false);

        return found ? Results.Ok(new { bountyId }) : Results.NotFound(new { code = "bounty.not_found" });
    }

    private static async Task<IResult> CloseBountyAsync(
        string bountyId, CloseBountyHandler handler, ICurrentUser user, IIdempotencyStore idempotency, HttpRequest http, CancellationToken ct)
    {
        var found = await idempotency.RunOnceAsync(
            $"bounty.close:{user.RequireUserId()}:{bountyId}", Read(http), IdempotencyRetention,
            () => handler.HandleAsync(bountyId, ct), ct).ConfigureAwait(false);

        return found ? Results.Ok(new { bountyId }) : Results.NotFound(new { code = "bounty.not_found" });
    }

    private sealed record ReportBountyRequest(string? Reason, string? Note);

    /// <summary>
    /// Reports a Dicari for staff to look at. The answer is the same whether this is the person's first report or a
    /// repeat, so it says nothing about what staff already hold.
    /// </summary>
    private static async Task<IResult> ReportBountyAsync(
        string bountyId, ReportBountyRequest request, ReportBountyHandler handler, ICurrentUser user,
        IIdempotencyStore idempotency, HttpRequest http, CancellationToken ct)
    {
        var found = await idempotency.RunOnceAsync(
            $"bounty.report:{user.RequireUserId()}:{bountyId}", Read(http), IdempotencyRetention,
            () => handler.HandleAsync(bountyId, request.Reason, request.Note, ct), ct).ConfigureAwait(false);

        return found ? Results.Ok(new { reported = true }) : Results.NotFound(new { code = "bounty.not_found" });
    }

    private static async Task<IResult> MakeProposalAsync(
        string bountyId,
        MakeProposalRequest request,
        MakeProposalHandler handler,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var proposalId = await idempotency.RunOnceAsync(
            $"bounty.propose:{user.RequireUserId()}:{bountyId}",
            Read(http),
            IdempotencyRetention,
            () => handler.HandleAsync(
                new MakeProposalCommand(bountyId, request.ListingId ?? string.Empty, request.PriceRupiah, request.Note), ct),
            ct,
            InFlightPolicy.Reject,
            RequestFingerprint.Of(request.ListingId, request.PriceRupiah, request.Note)).ConfigureAwait(false);

        return proposalId is null
            ? Results.NotFound(new { code = "bounty.not_found" })
            : Results.Ok(new { proposalId });
    }

    private static async Task<IResult> AcceptProposalAsync(
        string bountyId,
        string proposalId,
        AcceptProposalHandler handler,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        var accepted = await idempotency.RunOnceAsync(
            $"bounty.accept:{user.RequireUserId()}:{proposalId}", Read(http), IdempotencyRetention,
            () => handler.HandleAsync(bountyId, proposalId, ct), ct, InFlightPolicy.Reject).ConfigureAwait(false);

        return accepted is null
            ? Results.NotFound(new { code = "bounty.not_found" })
            : Results.Ok(new
            {
                accepted.BountyId,
                accepted.ProposalId,
                accepted.OfferId,
                priceRupiah = accepted.PriceRupiah,
                accepted.IsUpsell,
                amountOverBudgetRupiah = accepted.AmountOverBudgetRupiah,
                accepted.CheckoutBy,
            });
    }

    private static async Task<IResult> DeclineProposalAsync(
        string bountyId, string proposalId, DeclineProposalHandler handler, ICurrentUser user, IIdempotencyStore idempotency,
        HttpRequest http, CancellationToken ct)
    {
        var found = await idempotency.RunOnceAsync(
            $"bounty.decline:{user.RequireUserId()}:{proposalId}", Read(http), IdempotencyRetention,
            () => handler.HandleAsync(bountyId, proposalId, ct), ct).ConfigureAwait(false);

        return found ? Results.Ok(new { proposalId }) : Results.NotFound(new { code = "bounty.not_found" });
    }

    private static async Task<IResult> WithdrawProposalAsync(
        string bountyId, string proposalId, WithdrawProposalHandler handler, ICurrentUser user, IIdempotencyStore idempotency,
        HttpRequest http, CancellationToken ct)
    {
        var found = await idempotency.RunOnceAsync(
            $"bounty.withdraw:{user.RequireUserId()}:{proposalId}", Read(http), IdempotencyRetention,
            () => handler.HandleAsync(bountyId, proposalId, ct), ct).ConfigureAwait(false);

        return found ? Results.Ok(new { proposalId }) : Results.NotFound(new { code = "bounty.not_found" });
    }

    private static BountySort ParseSort(string? sort) => sort switch
    {
        "budget" => BountySort.BudgetHigh,
        "ending" => BountySort.EndingSoon,
        "fewest" => BountySort.FewestProposals,
        _ => BountySort.Newest,
    };

    /// <summary>
    /// A Dicari as a stranger sees it. The buyer is a handle, never an id; nothing here comes from a proposal.
    /// </summary>
    private static object ToCard(BountyCard card, IMediaStorage media) => new
    {
        card.Id,
        card.Code,
        card.Title,
        card.CategoryId,
        card.CategorySlug,
        card.Facets,
        maxBudgetRupiah = card.MaxBudgetRupiah,
        card.ShipToCity,
        buyerUsername = card.BuyerHandle,
        card.ProposalCount,
        card.CreatedAt,
        card.ExpiresAt,
        card.Status,
        photoKey = card.PhotoKey,
        photoUrl = media.OriginalUrl(card.PhotoKey),
    };

    private static object ToProposal(ProposalView p, IMediaStorage media) => new
    {
        p.Id,
        sellerUsername = p.SellerHandle,
        sellerMemberSince = p.SellerMemberSince,
        p.ListingId,
        p.ListingTitle,
        listingPhotoKey = p.ListingPhotoKey,
        listingPhotoUrl = media.OriginalUrl(p.ListingPhotoKey),
        listingCategorySlug = p.ListingCategorySlug,
        listingCondition = p.ListingCondition,
        listingCity = p.ListingCity,
        proposedPriceRupiah = p.ProposedPriceRupiah,
        listingPriceRupiah = p.ListingPriceRupiah,
        listingPriceAtProposalRupiah = p.ListingPriceAtProposalRupiah,
        p.IsUpsell,
        amountOverBudgetRupiah = p.AmountOverBudgetRupiah,
        p.Note,
        p.Status,
        p.LapseReason,
        p.CreatedAt,
        p.OfferId,
    };

    private sealed record PostBountyRequest(
        string? Title,
        string? CategoryId,
        Dictionary<string, string>? Facets,
        string? Description,
        List<string>? Conditions,
        long MaxBudgetRupiah,
        List<string>? PhotoKeys,
        int DurationDays);

    private sealed record EditBountyRequest(
        string? Title,
        string? Description,
        long MaxBudgetRupiah,
        List<string>? Conditions,
        List<string>? PhotoKeys,
        string? CategoryId,
        Dictionary<string, string>? Facets);

    private sealed record MakeProposalRequest(string? ListingId, long PriceRupiah, string? Note);
}
