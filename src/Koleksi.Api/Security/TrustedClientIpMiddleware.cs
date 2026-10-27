using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Koleksi.Api.Security;

/// <summary>
/// Lets the web tier tell this API which visitor a request is really on behalf of.
/// </summary>
/// <remarks>
/// <para>
/// The web Lambda calls this API over the public API Gateway, so from here every visitor
/// arrives from the web Lambda's own egress address. The address-keyed rate limits (sign-in
/// above all) would then be a single bucket for the whole site. The web tier therefore sends
/// the visitor's address in <see cref="ClientIpHeader"/>, alongside <see cref="SecretHeader"/>
/// carrying a shared secret.
/// </para>
/// <para>
/// The address is believed only when the secret matches. A direct caller who sets the address
/// header without knowing the secret changes nothing, which is the whole control: trusting the
/// header on its own would let anyone pick any rate-limit bucket, including a victim's. Both
/// values are compared as hashes so the comparison takes the same time whatever their lengths,
/// and the secret is never logged. Registered only when a secret is configured at all.
/// </para>
/// </remarks>
public sealed class TrustedClientIpMiddleware(RequestDelegate next, string internalSecret)
{
    public const string SecretHeader = "X-Koleksi-Internal";
    public const string ClientIpHeader = "X-Koleksi-Client-Ip";

    private readonly byte[] _secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(internalSecret));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var secret = context.Request.Headers[SecretHeader];
        var clientIp = context.Request.Headers[ClientIpHeader];

        // Exactly one value of each: a repeated header is ambiguous, so it is not trusted.
        if (secret.Count == 1 && clientIp.Count == 1
            && SecretMatches(secret[0])
            && IPAddress.TryParse(clientIp[0], out var address))
        {
            context.Connection.RemoteIpAddress = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        return next(context);
    }

    private bool SecretMatches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            _secretHash, SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
    }
}
