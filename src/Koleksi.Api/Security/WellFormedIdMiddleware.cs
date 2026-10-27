using Koleksi.Domain.Common;
using Microsoft.AspNetCore.Routing;

namespace Koleksi.Api.Security;

/// <summary>
/// Answers 404 to a route whose id segment cannot possibly be an id this service issued.
/// </summary>
/// <remarks>
/// <para>
/// Orders, offers and listings are 26-character <see cref="EntityId"/>s; a conversation's id is the 32 hex characters
/// <c>Conversation.IdFor</c> derives. The id lands in a DynamoDB key, and DynamoDB refuses a partition key over
/// 2,048 bytes, so a signed-in caller who sent a 3,000 character id used to get an unhandled 500 and an error-level log
/// line. Such a value names nothing, which is exactly what a 404 says, so it is refused here before it reaches a
/// repository.
/// </para>
/// <para>
/// Runs after authentication and authorization (an anonymous caller still gets the 401 an unknown route gets) and
/// only for routes that matched an endpoint. It does not touch <c>slugWithId</c> or <c>handle</c> parameters, which
/// have their own parsing, nor the watchlist, whose handlers already refuse a malformed id on their own terms (an
/// unsave of anything is a harmless no-op, by design).
/// </para>
/// </remarks>
internal sealed class WellFormedIdMiddleware(RequestDelegate next)
{
    private static readonly string[] EntityIdRouteKeys = ["orderId", "offerId", "listingId", "id"];

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Path.StartsWithSegments("/api/watchlist", StringComparison.OrdinalIgnoreCase)
            && !IdsAreWellFormed(context.Request.RouteValues))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return context.Response.WriteAsJsonAsync(new { code = "request.not_found" }, context.RequestAborted);
        }

        return next(context);
    }

    private static bool IdsAreWellFormed(RouteValueDictionary values)
    {
        foreach (var key in EntityIdRouteKeys)
        {
            if (values.TryGetValue(key, out var raw) && raw is string id && !EntityId.IsValid(id))
            {
                return false;
            }
        }

        return !values.TryGetValue("conversationId", out var conversation)
            || conversation is not string conversationId
            || IsConversationId(conversationId);
    }

    /// <summary>The 32 hex characters <c>Conversation.IdFor</c> produces.</summary>
    private static bool IsConversationId(string value) =>
        value.Length == 32 && value.All(char.IsAsciiHexDigit);
}
