using System.Net;
using Shouldly;

namespace Koleksi.AdminApi.Tests;

/// <summary>
/// What the admin API accepts as proof of identity.
/// </summary>
/// <remarks>
/// Every refused case here is a token that is correctly signed and structurally valid, minted by the same key the
/// handler trusts. That is the half of token validation worth testing: a garbage string is rejected by accident,
/// while a real token from the wrong pool, or an id token used as a credential, is rejected only because someone
/// configured the handler to notice.
/// </remarks>
public sealed class AdminAuthenticationTests(AdminApiFactory factory) : IClassFixture<AdminApiFactory>
{
    private const string SessionPath = "/api/admin/session";

    private const string AuditPath = "/api/admin/audit";

    [Theory]
    [InlineData(SessionPath, true)]
    [InlineData(AuditPath, false)]
    public async Task A_request_with_no_token_is_refused(string path, bool post)
    {
        using var client = factory.CreateWebTierClient();

        using var response = post ? await client.PostAsync(path, content: null) : await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_staff_token_is_accepted_and_names_its_own_subject()
    {
        using var client = factory.CreateStaffClient(AdminApiFactory.StaffBob, "bob");

        using var response = await client.PostAsync(SessionPath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entry = factory.Audit.Entries.ShouldHaveSingleItem();
        entry.ActorId.ShouldBe(AdminApiFactory.StaffBob);
        entry.ActorName.ShouldBe("bob");
    }

    [Fact]
    public async Task A_token_from_the_public_pool_is_refused()
    {
        // Same signing key, valid shape, valid lifetime, and even an access token: only the pool differs. This is the
        // isolation the whole separate-site design rests on, so it is tested with a token that is otherwise perfect.
        var token = Tokens.Access(factory.Key, new TokenSpec
        {
            Issuer = AdminApiFactory.PublicIssuer,
            ClientId = AdminApiFactory.PublicClientId,
        });
        using var client = factory.CreateWebTierClientWithToken(token);

        using var response = await client.PostAsync(SessionPath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_public_pool_token_cannot_read_the_audit_log_either()
    {
        var token = Tokens.Access(factory.Key, new TokenSpec
        {
            Issuer = AdminApiFactory.PublicIssuer,
            ClientId = AdminApiFactory.PublicClientId,
        });
        using var client = factory.CreateWebTierClientWithToken(token);

        using var response = await client.GetAsync(AuditPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_minted_for_another_client_of_the_staff_pool_is_refused()
    {
        // Same pool, same key, same issuer: only the client differs. Cognito access tokens carry no aud, so without the
        // explicit client_id check this would be indistinguishable from a valid one.
        var token = Tokens.Access(factory.Key, new TokenSpec { ClientId = "some-other-client" });
        using var client = factory.CreateWebTierClientWithToken(token);

        using var response = await client.PostAsync(SessionPath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_id_token_is_not_a_credential()
    {
        var token = Tokens.Access(factory.Key, new TokenSpec { TokenUse = "id" });
        using var client = factory.CreateWebTierClientWithToken(token);

        using var response = await client.PostAsync(SessionPath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var token = Tokens.Access(factory.Key, new TokenSpec
        {
            IssuedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Lifetime = TimeSpan.FromMinutes(15),
        });
        using var client = factory.CreateWebTierClientWithToken(token);

        using var response = await client.PostAsync(SessionPath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_signed_with_hmac_using_the_public_key_is_refused()
    {
        var token = Tokens.HmacSignedWithThePublicKey(factory.Key);
        using var client = factory.CreateWebTierClientWithToken(token);

        using var response = await client.PostAsync(SessionPath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_refused_request_writes_nothing_to_the_audit_log()
    {
        var before = factory.Audit.Entries.Count;
        using var client = factory.CreateWebTierClientWithToken(
            Tokens.Access(factory.Key, new TokenSpec { Issuer = AdminApiFactory.PublicIssuer }));

        using var response = await client.PostAsync(SessionPath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.Audit.Entries.Count.ShouldBe(before);
    }
}
