using System.Security.Claims;
using System.Text.RegularExpressions;
using Koleksi.Application.Features.Standing;

namespace Koleksi.Api.Security;

/// <summary>
/// Stops a banned account from starting anything new: any request that changes something is refused with
/// 403 <c>account.banned</c>.
/// </summary>
/// <remarks>
/// <para>
/// A ban is for breaking commitments, so it stops new activity (buying, bidding, offering, listing, chatting,
/// editing a profile) but never traps money that is already in motion. These still work: signing out and
/// refreshing the session, and the steps of an order that already exists (shipping it, confirming receipt,
/// disputing, cancelling an unpaid one, reviewing). Everything read-only works too, so the account can see why.
/// </para>
/// <para>
/// Runs after authentication, so only signed-in callers are looked at; the payment webhook and the sweeps are
/// anonymous and never reach this. The ban status is cached for a few seconds per process
/// (<see cref="BanStatusCache"/>) so this is not a database read on every request.
/// </para>
/// </remarks>
internal sealed partial class BannedAccountMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    /// <summary>What a banned account may still do: leave, and finish orders that already exist.</summary>
    [GeneratedRegex(
        @"^/api/(auth/(logout|refresh)|realtime/tickets|orders/[^/]+/(shipment|release|dispute|cancel|review))/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex StillAllowed();

    public async Task InvokeAsync(HttpContext context, AccountStandingService standing)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (SafeMethods.Contains(context.Request.Method)
            || context.User.FindFirstValue("sub") is not { Length: > 0 } userId
            || StillAllowed().IsMatch(context.Request.Path.Value ?? string.Empty))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (await standing.IsBannedAsync(userId, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { code = "account.banned" }, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
