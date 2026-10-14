using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Messaging;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Messaging;
using Koleksi.Domain.Users;
using Microsoft.AspNetCore.RateLimiting;

using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>A buyer and a seller talking about a listing.</summary>
/// <remarks>
/// <para>
/// No <c>AllowAnonymous</c> anywhere in this group: every route needs an identity, and the
/// participant check happens in the handler, never from the route. A conversation that does
/// not exist and one the caller is not part of both answer <c>404</c>, so a conversation id
/// cannot be probed.
/// </para>
/// <para>
/// Responses never carry a user id. Each message says whether it is the caller's
/// (<c>isMine</c>) and the conversation names the <em>other</em> party's handle, which is all
/// the screen needs and nothing it could use to address someone outside the conversation.
/// </para>
/// </remarks>
internal static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/conversations").WithTags("Conversations");

        group.MapPost("", StartConversationAsync)
            .RequireRateLimiting(RateLimitPolicies.Messaging)
            .WithName("StartConversation");
        group.MapGet("", ListConversationsAsync).WithName("ListConversations");
        group.MapGet("/{conversationId}", GetConversationAsync).WithName("GetConversation");
        group.MapPost("/{conversationId}/messages", SendMessageAsync)
            .RequireRateLimiting(RateLimitPolicies.Messaging)
            .WithName("SendMessage");
        group.MapPost("/{conversationId}/read", MarkReadAsync).WithName("MarkConversationRead");

        // The caller's own conversation about one listing, for the item page. Registered on its
        // own rather than in the group above because the listing, not a conversation id, is
        // what the caller has in hand.
        app.MapGet("/api/listings/{listingId}/conversation", GetListingConversationAsync)
            .WithTags("Conversations")
            .WithName("GetListingConversation");

        return app;
    }

    private static async Task<IResult> StartConversationAsync(
        StartConversationRequest request,
        StartConversationHandler handler,
        IIdempotencyStore idempotency,
        ICurrentUser user,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ListingId))
        {
            return Results.NotFound(new { code = "chat.listing_not_found" });
        }

        // A double-tap on "Send" must not post the same message twice.
        var result = await idempotency.RunOnceAsync(
            $"chat.start:{user.RequireUserId()}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => handler.HandleAsync(new StartConversationCommand(request.ListingId, request.Body), ct),
            ct).ConfigureAwait(false);

        return result.Found
            ? Results.Ok(new { conversationId = result.ConversationId, messageId = result.MessageId })
            : Results.NotFound(new { code = "chat.listing_not_found" });
    }

    private static async Task<IResult> SendMessageAsync(
        string conversationId,
        SendMessageRequest request,
        SendMessageHandler handler,
        IIdempotencyStore idempotency,
        ICurrentUser user,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await idempotency.RunOnceAsync(
            $"chat.send:{user.RequireUserId()}:{conversationId}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => handler.HandleAsync(conversationId, request.Body, ct),
            ct).ConfigureAwait(false);

        return result.Found
            ? Results.Ok(new { conversationId = result.ConversationId, messageId = result.MessageId })
            : Results.NotFound(new { code = "chat.not_found" });
    }

    private static async Task<IResult> ListConversationsAsync(
        ListConversationsHandler handler, ICurrentUser user, PublicIdentityResolver identities, CancellationToken ct)
    {
        var viewerId = user.RequireUserId();
        var conversations = await handler.HandleAsync(ct).ConfigureAwait(false);

        var names = await ResolveOtherPartiesAsync(identities, conversations, viewerId, ct).ConfigureAwait(false);
        var items = conversations.Select(conversation => ToSummary(conversation, viewerId, names)).ToList();

        return Results.Ok(new { items, totalUnread = conversations.Sum(c => c.UnreadFor(viewerId)) });
    }

    private static async Task<IResult> GetConversationAsync(
        string conversationId,
        int? limit,
        GetConversationHandler handler,
        ICurrentUser user,
        PublicIdentityResolver identities,
        CancellationToken ct) =>
        await ToThreadResultAsync(
            await handler.HandleAsync(conversationId, limit, ct).ConfigureAwait(false), user, identities, ct)
            .ConfigureAwait(false);

    private static async Task<IResult> GetListingConversationAsync(
        string listingId,
        int? limit,
        GetConversationHandler handler,
        ICurrentUser user,
        PublicIdentityResolver identities,
        CancellationToken ct) =>
        await ToThreadResultAsync(
            await handler.HandleForListingAsync(listingId, limit, ct).ConfigureAwait(false), user, identities, ct)
            .ConfigureAwait(false);

    private static async Task<IResult> ToThreadResultAsync(
        ConversationThread? thread, ICurrentUser user, PublicIdentityResolver identities, CancellationToken ct)
    {
        if (thread is null)
        {
            return Results.NotFound(new { code = "chat.not_found" });
        }

        var viewerId = user.RequireUserId();
        var names = await ResolveOtherPartiesAsync(identities, [thread.Conversation], viewerId, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            conversation = ToSummary(thread.Conversation, viewerId, names),
            viewerRole = thread.ViewerRole.ToString(),
            messages = thread.Messages.Select(message => new
            {
                message.Id,
                message.Body,
                message.SentAt,
                isMine = string.Equals(message.SenderId, viewerId, StringComparison.Ordinal),
            }),
        });
    }

    private static async Task<IResult> MarkReadAsync(
        string conversationId, MarkConversationReadHandler handler, CancellationToken ct) =>
        await handler.HandleAsync(conversationId, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound(new { code = "chat.not_found" });

    private static string OtherPartyId(Conversation conversation, string viewerId) =>
        conversation.RoleOf(viewerId) == ConversationRole.Buyer ? conversation.SellerId : conversation.BuyerId;

    /// <summary>
    /// The other party's current public handle for each conversation. The names stored on the
    /// conversation were the sign-in names when it began, which for an email account is the email
    /// address, so they are never sent.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, PublicProfile>> ResolveOtherPartiesAsync(
        PublicIdentityResolver identities,
        IEnumerable<Conversation> conversations,
        string viewerId,
        CancellationToken ct) =>
        await identities
            .ResolveManyAsync(conversations.Select(conversation => OtherPartyId(conversation, viewerId)), ct)
            .ConfigureAwait(false);

    private static object ToSummary(
        Conversation conversation, string viewerId, IReadOnlyDictionary<string, PublicProfile> names) => new
    {
        conversation.Id,
        conversation.ListingId,
        conversation.ListingTitle,
        conversation.ListingPhotoKey,
        otherPartyUsername = names[OtherPartyId(conversation, viewerId)].ShownName,
        unread = conversation.UnreadFor(viewerId),
        lastMessagePreview = conversation.LastMessagePreview,
        lastMessageAt = conversation.LastMessageAt,
        lastMessageIsMine = string.Equals(conversation.LastSenderId, viewerId, StringComparison.Ordinal),
    };

    private sealed record StartConversationRequest(string ListingId, string? Body);

    private sealed record SendMessageRequest(string? Body);
}
