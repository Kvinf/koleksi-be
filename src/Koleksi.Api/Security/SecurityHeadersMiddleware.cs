namespace Koleksi.Api.Security;

/// <summary>
/// Applies the response headers that constrain what a browser will do with our output.
/// </summary>
/// <remarks>
/// <para>
/// These are cheap and they fail safe, so they are applied to every response including
/// errors — a 500 that renders attacker-controlled text is still a cross-site scripting
/// hole if the browser is willing to sniff it as HTML.
/// </para>
/// <para>
/// The API serves only JSON, which shapes the choices below: the content security
/// policy denies everything, because a JSON response has no legitimate reason to load a
/// script, a frame, or an image. The frontend serves its own, much larger policy.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // OnStarting rather than setting headers now: the response has not been written
        // yet, and a later handler could otherwise replace what we set.
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;

            // Honour the declared Content-Type instead of guessing. Without this, a
            // browser may sniff a JSON body containing HTML and execute it.
            headers["X-Content-Type-Options"] = "nosniff";

            // The API is never a frame's document. Clickjacking has no purchase on JSON,
            // but the header costs nothing and covers any future HTML error page.
            headers["X-Frame-Options"] = "DENY";

            // Deny by default. An API response should load nothing at all.
            headers["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

            // Send the origin but not the path on cross-origin navigations, so listing
            // ids and search terms do not leak into third-party referer logs.
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            // The API needs none of these device capabilities.
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

            // Responses are per-user; a shared cache must never serve one user's order
            // list to another. Individual endpoints opt public data back in explicitly.
            if (!headers.ContainsKey("Cache-Control"))
            {
                headers["Cache-Control"] = "no-store";
            }

            // Advertises the server product and version to anyone fingerprinting us.
            headers.Remove("Server");

            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}

/// <summary>Wires <see cref="SecurityHeadersMiddleware"/> into the pipeline.</summary>
public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
