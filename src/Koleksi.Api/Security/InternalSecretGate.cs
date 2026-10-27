using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Koleksi.Api.Security;

/// <summary>
/// The yes/no check for a caller that claims to be this system's own scheduler or web tier.
/// </summary>
/// <remarks>
/// <para>
/// Fails closed: with no secret configured nobody is authorised, so an environment that never set
/// <c>Koleksi:Security:InternalSecret</c> has no internal door at all rather than an open one.
/// Exactly one header value is accepted (a repeated header is ambiguous), and both sides are
/// compared as SHA-256 hashes in fixed time so neither the secret's length nor how much of it
/// matched can be measured from the response time. The secret is never logged.
/// </para>
/// <para>
/// The same shape as <see cref="TrustedClientIpMiddleware"/>'s check, which precomputes the hash
/// once because it runs on every request; this runs only on the rare internal call.
/// </para>
/// </remarks>
public static class InternalSecretGate
{
    public static bool IsAuthorised(string? configuredSecret, StringValues presented)
    {
        if (string.IsNullOrWhiteSpace(configuredSecret) || presented.Count != 1 || string.IsNullOrEmpty(presented[0]))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented[0]!)));
    }
}
