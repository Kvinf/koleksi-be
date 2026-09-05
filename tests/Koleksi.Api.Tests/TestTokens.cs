using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Koleksi.Api.Tests;

/// <summary>
/// An RSA key pair this test process owns, standing in for the Cognito pool's JWKS.
/// </summary>
/// <remarks>
/// Substituting the key rather than the authentication handler is the whole point. A test
/// that swaps in a fake authentication scheme proves only that the fake works; every rule
/// worth having — RS256 only, the issuer, the <c>client_id</c> check, the refusal of id
/// tokens, the thirty-second clock skew — lives in the real handler's configuration and is
/// exercised here against tokens that are genuinely signed.
/// </remarks>
public sealed class TestSigningKey : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public TestSigningKey(string keyId) => Key = new RsaSecurityKey(_rsa) { KeyId = keyId };

    public RsaSecurityKey Key { get; }

    /// <summary>The public half, as SubjectPublicKeyInfo — the material a JWKS publishes.</summary>
    public byte[] PublicKeyBytes => _rsa.ExportSubjectPublicKeyInfo();

    public void Dispose() => _rsa.Dispose();
}

/// <summary>How one minted token should differ from a well-formed one.</summary>
/// <remarks>
/// Every field defaults to the valid value, so a test names only the thing it is attacking
/// with. That keeps "this token is rejected because its <c>token_use</c> is wrong" from
/// quietly becoming "this token is rejected because six things about it are wrong".
/// </remarks>
public sealed record TokenOptions
{
    /// <summary>The Cognito subject. This is the identity every handler acts on.</summary>
    public string Subject { get; init; } = "01JBIDDERAAAAAAAAAAAAAAAAA";

    public string Username { get; init; } = "rangga.wh";

    public string Issuer { get; init; } = KoleksiApiFactory.Issuer;

    public string ClientId { get; init; } = KoleksiApiFactory.ClientId;

    /// <summary>
    /// Cognito stamps <c>access</c> or <c>id</c>. Only the first authorises an API call.
    /// </summary>
    public string TokenUse { get; init; } = "access";

    /// <summary>Cognito group membership, emitted as a repeated <c>cognito:groups</c> claim.</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// When the token was minted, in real wall-clock terms.
    /// </summary>
    /// <remarks>
    /// Lifetime validation happens inside the identity library against the system clock,
    /// not against the injected <see cref="Fakes.MutableClock"/>, so an expired-token test
    /// has to move this rather than the domain clock.
    /// </remarks>
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Mints the access tokens the integration tests present.</summary>
public static class TestTokens
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>A well-formed Cognito-shaped access token, signed RS256.</summary>
    public static string Access(TestSigningKey key, TokenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        options ??= new TokenOptions();

        return Handler.CreateToken(Describe(
            options, new SigningCredentials(key.Key, SecurityAlgorithms.RsaSha256)));
    }

    /// <summary>
    /// The algorithm-confusion attack: an HS256 token whose shared secret is the public
    /// key everyone can read from the JWKS.
    /// </summary>
    /// <remarks>
    /// A validator that trusts the token's own <c>alg</c> header will happily verify this
    /// with material the attacker also has, which is why the API pins
    /// <c>ValidAlgorithms</c> to RS256 rather than leaving the list open.
    /// </remarks>
    public static string HmacSignedWithThePublicKey(TestSigningKey key, TokenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        options ??= new TokenOptions();

        var secret = new SymmetricSecurityKey(key.PublicKeyBytes) { KeyId = key.Key.KeyId };

        return Handler.CreateToken(Describe(
            options, new SigningCredentials(secret, SecurityAlgorithms.HmacSha256)));
    }

    private static SecurityTokenDescriptor Describe(TokenOptions options, SigningCredentials credentials)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = options.Subject,
            ["cognito:username"] = options.Username,
            ["client_id"] = options.ClientId,
            ["token_use"] = options.TokenUse,

            // Cognito emits one; including it keeps the token shape honest.
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        if (options.Groups.Count > 0)
        {
            claims["cognito:groups"] = options.Groups;
        }

        return new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            IssuedAt = options.IssuedAt.UtcDateTime,
            NotBefore = options.IssuedAt.UtcDateTime,
            Expires = (options.IssuedAt + options.Lifetime).UtcDateTime,
            SigningCredentials = credentials,
            Claims = claims,
        };
    }
}
