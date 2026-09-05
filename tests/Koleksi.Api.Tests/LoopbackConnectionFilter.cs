using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Koleksi.Api.Tests;

/// <summary>
/// Gives the test host's requests a remote address, as a real socket would.
/// </summary>
/// <remarks>
/// <para>
/// <c>TestServer</c> synthesises requests rather than accepting them, so
/// <c>Connection.RemoteIpAddress</c> is null. That is not a cosmetic gap: the
/// forwarded-headers middleware only honours <c>X-Forwarded-For</c> when the immediate
/// peer is a known proxy, and a null peer is not known — so without this every
/// anonymous request lands in one rate-limit partition and the per-caller partitioning
/// cannot be tested at all.
/// </para>
/// <para>
/// Loopback is the honest choice, because it is what the peer address genuinely is when
/// something on the same machine connects. It is also inside the default known-networks
/// range, so the real <c>UseForwardedHeaders</c> configuration in <c>Program</c> — not a
/// substitute for it — is what turns the header into an address.
/// </para>
/// <para>
/// Registered as a startup filter so it wraps the entire pipeline and runs before the
/// first line of <c>Program</c>'s, which is where a proxy's own peer address would
/// already be present in production.
/// </para>
/// </remarks>
internal sealed class LoopbackConnectionFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
            await continuation().ConfigureAwait(false);
        });

        next(app);
    };
}
