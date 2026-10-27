using System.Globalization;

namespace Koleksi.Infrastructure;

/// <summary>
/// Every value the API needs from its environment, bound from configuration at startup.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Validate"/> runs before the host is built and throws on anything missing or
/// malformed, so a misconfigured deployment fails to start rather than starting in a
/// weakened state. That distinction is the whole point of this type: an empty
/// <see cref="CognitoOptions.Authority"/> that is discovered lazily on the first request
/// means an API that accepted traffic while unable to validate a single token.
/// </para>
/// <para>
/// Nothing here is a secret. Bucket names, table names, and pool ids are identifiers, not
/// credentials — the Lambda's IAM role decides what it may do with them. The one true
/// secret, the payment gateway's webhook key, is read from Secrets Manager by ARN at
/// startup and is never written to configuration or to an environment variable.
/// </para>
/// </remarks>
public sealed class KoleksiOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Koleksi";

    /// <summary>AWS region for every SDK client. Empty means "use the ambient default".</summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>
    /// The deployment stage (`dev`, `staging`, `prod`), set by CDK on every deployed stage. Empty locally. Used for
    /// exactly one thing today: refusing to start with the simulators on anywhere but dev.
    /// </summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>The stand-ins for the payment gateway, the courier and the payout provider. Off unless asked for.</summary>
    public SimulatorOptions Simulators { get; init; } = new();

    public CognitoOptions Cognito { get; init; } = new();

    public DynamoOptions Dynamo { get; init; } = new();

    public MediaOptions Media { get; init; } = new();

    public EmailOptions Email { get; init; } = new();

    public PaymentOptions Payment { get; init; } = new();

    /// <summary>The courier-tracking provider, if one is chosen. Absent means only the buyer's confirmation releases escrow.</summary>
    public DeliveryOptions Delivery { get; init; } = new();

    public CorsOptions Cors { get; init; } = new();

    public RealtimeOptions Realtime { get; init; } = new();

    public SecurityOptions Security { get; init; } = new();

    /// <summary>Built-but-off product features. See <see cref="FeaturesOptions"/>.</summary>
    public FeaturesOptions Features { get; init; } = new();

    /// <summary>
    /// Fails fast on a configuration that cannot serve traffic safely in a deployed stage.
    /// </summary>
    /// <exception cref="InvalidOperationException">Any required value is missing or malformed.</exception>
    public void Validate() => Validate(allowLocalEndpoints: false);

    /// <summary>
    /// Fails fast on a configuration that cannot serve traffic safely.
    /// </summary>
    /// <param name="allowLocalEndpoints">
    /// Whether development-only overrides such as <see cref="DynamoOptions.ServiceUrl"/> are
    /// permitted. Passed from the host environment by the composition root, so that pointing
    /// the API at a machine on someone's desk is impossible outside development rather than
    /// merely discouraged by a comment.
    /// </param>
    /// <exception cref="InvalidOperationException">Any required value is missing or malformed.</exception>
    public void Validate(bool allowLocalEndpoints)
    {
        var problems = new List<string>();

        // Resolve the region fallback before anything reads Cognito.Authority. Doing it
        // here rather than in the binder keeps "configuration is valid" and "configuration
        // is fully resolved" as one step that either succeeds or throws.
        Cognito.ResolvedRegion = string.IsNullOrWhiteSpace(Cognito.Region) ? Region : Cognito.Region;

        Cognito.Validate(problems);
        Dynamo.Validate(problems, allowLocalEndpoints);
        Media.Validate(problems, allowLocalEndpoints);
        Email.Validate(problems);
        Payment.Validate(problems);
        Delivery.Validate(problems);
        Simulators.Validate(problems, Stage, allowLocalEndpoints);
        Cors.Validate(problems);
        Realtime.Validate(problems);
        Security.Validate(problems);

        if (problems.Count > 0)
        {
            // Every problem at once. Reporting them one deployment at a time turns a
            // five-minute fix into five cold starts.
            throw new InvalidOperationException(
                $"Configuration section '{SectionName}' is invalid:{Environment.NewLine}  - "
                + string.Join($"{Environment.NewLine}  - ", problems)
                + $"{Environment.NewLine}See docs/configuration.md.");
        }
    }

    internal static void RequireText(List<string> problems, string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{SectionName}:{path} is required.");
        }
    }

    internal static void RequireAbsoluteHttpsUrl(List<string> problems, string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{SectionName}:{path} is required.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            problems.Add($"{SectionName}:{path} must be an absolute URL.");
            return;
        }

        // Plaintext here would mean fetching signing keys, or redirecting a paying buyer,
        // over a channel any intermediary can rewrite.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            problems.Add($"{SectionName}:{path} must use https.");
        }
    }
}

/// <summary>The Cognito user pool that mints and signs access tokens.</summary>
public sealed class CognitoOptions
{
    /// <summary>Region the user pool lives in. Falls back to <see cref="KoleksiOptions.Region"/>.</summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>User pool id, e.g. <c>ap-southeast-3_ab12cd34e</c>.</summary>
    public string UserPoolId { get; init; } = string.Empty;

    /// <summary>
    /// The app client the frontend authenticates with.
    /// </summary>
    /// <remarks>
    /// Checked against the token's <c>client_id</c> claim on every request. Without that
    /// check, a token minted for any other client of the same pool — including one an
    /// attacker registered for a different app — would be accepted here.
    /// </remarks>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// The token issuer, which is also where the JWKS is published.
    /// </summary>
    /// <remarks>
    /// Derived rather than configured, so it cannot drift out of step with the pool id and
    /// cannot be pointed at an issuer we do not control by editing one setting.
    /// </remarks>
    public string Authority => $"https://cognito-idp.{ResolvedRegion}.amazonaws.com/{UserPoolId}";

    internal string ResolvedRegion { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the pool's hosted-UI domain, e.g.
    /// <c>https://koleksi-dev.auth.ap-southeast-3.amazoncognito.com</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where <c>/oauth2/authorize</c> and <c>/oauth2/token</c> live — not under the
    /// pool itself — so it is what Google sign-in's authorization-code exchange POSTs to.
    /// It is the exact value the CDK stack's <c>HostedUiDomain</c> output prints; see
    /// <c>infra/lib/auth-stack.ts</c>.
    /// </para>
    /// <para>
    /// Optional, unlike everything else on this type: Google sign-in is not configured until
    /// a real client id and secret exist (see <c>AuthStackProps.googleClientId</c>), and a
    /// deployment with no Google identity provider has no use for this value. When absent,
    /// <see cref="IIdentityService.ExchangeAuthorizationCodeAsync"/> simply has nothing to
    /// call and reports failure rather than the startup refusing to boot over a feature that
    /// is not turned on yet.
    /// </para>
    /// </remarks>
    public string? HostedUiDomain { get; init; }

    internal void Validate(List<string> problems)
    {
        KoleksiOptions.RequireText(problems, UserPoolId, "Cognito:UserPoolId");
        KoleksiOptions.RequireText(problems, ClientId, "Cognito:ClientId");

        if (!string.IsNullOrWhiteSpace(UserPoolId) && !UserPoolId.Contains('_', StringComparison.Ordinal))
        {
            problems.Add($"{KoleksiOptions.SectionName}:Cognito:UserPoolId must look like 'region_poolid'.");
        }

        if (string.IsNullOrWhiteSpace(ResolvedRegion))
        {
            problems.Add($"{KoleksiOptions.SectionName}:Cognito:Region (or {KoleksiOptions.SectionName}:Region) is required.");
        }

        // Shape-checked only when present — see the property's doc comment for why it is
        // not required.
        if (!string.IsNullOrWhiteSpace(HostedUiDomain))
        {
            KoleksiOptions.RequireAbsoluteHttpsUrl(problems, HostedUiDomain, "Cognito:HostedUiDomain");
        }
    }
}

/// <summary>The single DynamoDB table holding every aggregate.</summary>
public sealed class DynamoOptions
{
    public string TableName { get; init; } = string.Empty;

    /// <summary>
    /// Local DynamoDB endpoint for development.
    /// </summary>
    /// <remarks>
    /// Refused outside development, so nobody can point a deployed stage at an endpoint of
    /// their choosing — and read or write the marketplace's data through it — by editing a
    /// single environment variable.
    /// </remarks>
    public string? ServiceUrl { get; init; }

    internal void Validate(List<string> problems, bool allowLocalEndpoints)
    {
        KoleksiOptions.RequireText(problems, TableName, "Dynamo:TableName");

        if (string.IsNullOrWhiteSpace(ServiceUrl))
        {
            return;
        }

        if (!allowLocalEndpoints)
        {
            problems.Add(
                $"{KoleksiOptions.SectionName}:Dynamo:ServiceUrl is a development-only override "
                + "and must not be set in a deployed stage.");
        }

        // The scheme is checked, not merely absoluteness. "localhost:8000" parses as an
        // absolute URI whose scheme is "localhost", so a bare host and port would pass an
        // absoluteness check and then fail later with a message about nothing in particular.
        if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add(
                $"{KoleksiOptions.SectionName}:Dynamo:ServiceUrl must be an absolute http or https URL, "
                + "e.g. 'http://localhost:8000'.");
        }
    }
}

/// <summary>Live updates pushed to open browser pages.</summary>
public sealed class RealtimeOptions
{
    /// <summary>
    /// The WebSocket API's connection-management endpoint, e.g.
    /// <c>https://abc123.execute-api.ap-southeast-3.amazonaws.com/dev</c> — where this service
    /// posts an event to reach a connected browser.
    /// </summary>
    /// <remarks>
    /// Optional. Unset, nothing is pushed: the pages fall back to polling, which is exactly
    /// what a stage without the WebSocket API (or a local run that has not enabled it) gets.
    /// </remarks>
    public string? ManagementUrl { get; init; }

    internal void Validate(List<string> problems)
    {
        if (!string.IsNullOrWhiteSpace(ManagementUrl))
        {
            KoleksiOptions.RequireAbsoluteHttpsUrl(problems, ManagementUrl, "Realtime:ManagementUrl");
        }
    }
}

/// <summary>Object storage for user-uploaded media.</summary>
public sealed class MediaOptions
{
    /// <summary>Bucket behind the CDN. Listing photos, avatars, review photos.</summary>
    public string PublicBucket { get; init; } = string.Empty;

    /// <summary>
    /// Bucket for identity documents.
    /// </summary>
    /// <remarks>
    /// Separate on purpose, and separately validated: a KTP scan and a photo of a Gunpla
    /// box must not be one policy change away from sharing a distribution. If someone ever
    /// points both at the same bucket, startup fails here rather than in a breach report.
    /// </remarks>
    public string PrivateBucket { get; init; } = string.Empty;

    /// <summary>CDN origin that serves <see cref="PublicBucket"/>.</summary>
    /// <remarks>
    /// Must be https, unless <see cref="ServiceUrl"/> is also set — a local S3-compatible
    /// endpoint has no CDN in front of it, so this points straight at the object store's
    /// own bucket URL instead, e.g. <c>http://localhost:9000/koleksi-dev-media</c>.
    /// </remarks>
    public string CdnBaseUrl { get; init; } = string.Empty;

    /// <summary>How long an upload grant stays usable.</summary>
    /// <remarks>
    /// Short enough that a leaked URL is worthless quickly, long enough for a phone photo
    /// on a slow connection. Fifteen minutes matches the presigned-URL default.
    /// </remarks>
    public TimeSpan UploadGrantLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Local S3-compatible endpoint for development, e.g. MinIO.
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="DynamoOptions.ServiceUrl"/> and refused outside development
    /// for the same reason: pointing a deployed stage at an object store of someone's
    /// choosing would let it read and overwrite every bucket the app uses. Set, it makes
    /// <c>S3MediaStorage</c> issue presigned grants against that endpoint instead of real
    /// S3 — real presigned uploads, real signature verification, just against MinIO — so
    /// local development exercises the same code path a deployed stage does. Unset, the API
    /// falls back to the in-memory <c>LocalMediaStorage</c> stand-in.
    /// </remarks>
    public string? ServiceUrl { get; init; }

    internal void Validate(List<string> problems, bool allowLocalEndpoints)
    {
        KoleksiOptions.RequireText(problems, PublicBucket, "Media:PublicBucket");
        KoleksiOptions.RequireText(problems, PrivateBucket, "Media:PrivateBucket");

        if (string.IsNullOrWhiteSpace(ServiceUrl))
        {
            KoleksiOptions.RequireAbsoluteHttpsUrl(problems, CdnBaseUrl, "Media:CdnBaseUrl");
        }
        else
        {
            if (!allowLocalEndpoints)
            {
                problems.Add(
                    $"{KoleksiOptions.SectionName}:Media:ServiceUrl is a development-only override "
                    + "and must not be set in a deployed stage.");
            }

            if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                problems.Add(
                    $"{KoleksiOptions.SectionName}:Media:ServiceUrl must be an absolute http or https URL, "
                    + "e.g. 'http://localhost:9000'.");
            }

            // A local endpoint has no CDN, so the bucket URL itself is served over plain
            // http — only permitted here because ServiceUrl already gated this branch to
            // development.
            if (string.IsNullOrWhiteSpace(CdnBaseUrl) || !Uri.TryCreate(CdnBaseUrl, UriKind.Absolute, out _))
            {
                problems.Add($"{KoleksiOptions.SectionName}:Media:CdnBaseUrl must be an absolute URL.");
            }
        }

        if (!string.IsNullOrWhiteSpace(PublicBucket)
            && string.Equals(PublicBucket, PrivateBucket, StringComparison.Ordinal))
        {
            problems.Add(
                $"{KoleksiOptions.SectionName}:Media:PrivateBucket must differ from PublicBucket — "
                + "identity documents must not live in a CDN-backed bucket.");
        }

        if (UploadGrantLifetime <= TimeSpan.Zero || UploadGrantLifetime > TimeSpan.FromHours(1))
        {
            problems.Add(
                $"{KoleksiOptions.SectionName}:Media:UploadGrantLifetime must be between 1 second and 1 hour "
                + $"(was {UploadGrantLifetime.ToString("c", CultureInfo.InvariantCulture)}).");
        }
    }
}

/// <summary>Outbound verification email, sent via SES.</summary>
public sealed class EmailOptions
{
    /// <summary>
    /// The address SES sends from — must be a verified identity (or domain) in the SES
    /// account, or SES refuses to send at all.
    /// </summary>
    public string FromAddress { get; init; } = string.Empty;

    internal void Validate(List<string> problems) =>
        KoleksiOptions.RequireText(problems, FromAddress, "Email:FromAddress");
}

/// <summary>The third-party payment processor.</summary>
public sealed class PaymentOptions
{
    /// <summary>Processor API base, e.g. the sandbox or production host.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Merchant/client identifier issued by the processor. Not a secret.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Secrets Manager ARN holding the shared key used to verify webhook signatures.
    /// </summary>
    /// <remarks>
    /// An ARN, not the key. The key is fetched at startup with the Lambda's role and kept
    /// in memory only, so it never appears in a template, a build artefact, an environment
    /// variable, or a CloudWatch log line.
    /// </remarks>
    public string SecretArn { get; init; } = string.Empty;

    /// <summary>Where the processor sends the buyer after payment.</summary>
    public string ReturnUrl { get; init; } = string.Empty;

    internal void Validate(List<string> problems)
    {
        KoleksiOptions.RequireAbsoluteHttpsUrl(problems, BaseUrl, "Payment:BaseUrl");
        KoleksiOptions.RequireText(problems, ClientId, "Payment:ClientId");
        KoleksiOptions.RequireText(problems, SecretArn, "Payment:SecretArn");
        KoleksiOptions.RequireAbsoluteHttpsUrl(problems, ReturnUrl, "Payment:ReturnUrl");

        if (!string.IsNullOrWhiteSpace(SecretArn)
            && !SecretArn.StartsWith("arn:aws:secretsmanager:", StringComparison.Ordinal))
        {
            problems.Add(
                $"{KoleksiOptions.SectionName}:Payment:SecretArn must be a Secrets Manager ARN — "
                + "the webhook key must never be configured inline.");
        }
    }
}

/// <summary>
/// The stand-ins that let an order be walked through payment, shipping and payout by hand while the real payment
/// gateway, courier and payout provider are not connected.
/// </summary>
/// <remarks>
/// Off by default, and refused outright (the host does not start) unless the stage is <c>dev</c> or the host is a
/// developer's own machine. A deployed staging or production stage can never run with them on, whatever its
/// environment says: the stage is set by CDK, and CDK itself refuses to set the flag outside dev.
/// </remarks>
public sealed class SimulatorOptions
{
    public bool Enabled { get; init; }

    internal void Validate(List<string> problems, string stage, bool allowLocalEndpoints)
    {
        if (Enabled && !allowLocalEndpoints && !string.Equals(stage, "dev", StringComparison.Ordinal))
        {
            problems.Add(
                $"{KoleksiOptions.SectionName}:Simulators:Enabled is only allowed on a developer's machine or the dev stage "
                + $"(`{KoleksiOptions.SectionName}:Stage` is '{(string.IsNullOrWhiteSpace(stage) ? "unset" : stage)}'). "
                + "They stand in for payment and delivery and must never run where real money moves.");
        }
    }
}

/// <summary>The third-party courier-tracking provider.</summary>
/// <remarks>
/// Courier tracking is not something the payment gateway provides, so it has its own provider and its own
/// credentials. <c>Provider</c> names which adapter is wired; the default <c>none</c> keeps today's behaviour
/// (the buyer confirms receipt) and needs nothing else set.
/// </remarks>
public sealed class DeliveryOptions
{
    public const string None = "none";

    /// <summary>Provider adapter name: <c>none</c>, or one registered in <c>AddDeliveryProvider</c> (today <c>signed-json</c>).</summary>
    public string Provider { get; init; } = None;

    /// <summary>The provider's API base, for the adapters that ask or register parcels. Https only.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Secrets Manager ARN of the key that signs the provider's webhooks. An ARN, never the key.</summary>
    public string WebhookSecretArn { get; init; } = string.Empty;

    /// <summary>Secrets Manager ARN of the API key used to call the provider. An ARN, never the key.</summary>
    public string ApiKeySecretArn { get; init; } = string.Empty;

    public bool IsConfigured => !string.Equals(Provider, None, StringComparison.OrdinalIgnoreCase);

    internal void Validate(List<string> problems)
    {
        if (!IsConfigured)
        {
            return;
        }

        KoleksiOptions.RequireText(problems, WebhookSecretArn, "Delivery:WebhookSecretArn");
        RequireArn(problems, WebhookSecretArn, "Delivery:WebhookSecretArn");

        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            KoleksiOptions.RequireAbsoluteHttpsUrl(problems, BaseUrl, "Delivery:BaseUrl");
        }

        if (!string.IsNullOrWhiteSpace(ApiKeySecretArn))
        {
            RequireArn(problems, ApiKeySecretArn, "Delivery:ApiKeySecretArn");
        }
    }

    private static void RequireArn(List<string> problems, string value, string name)
    {
        if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("arn:aws:secretsmanager:", StringComparison.Ordinal))
        {
            problems.Add($"{KoleksiOptions.SectionName}:{name} must be a Secrets Manager ARN: a key is never configured inline.");
        }
    }
}

/// <summary>Browser origins permitted to call this API with credentials.</summary>
public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; init; } = [];

    internal void Validate(List<string> problems)
    {
        if (AllowedOrigins.Length == 0)
        {
            problems.Add($"{KoleksiOptions.SectionName}:Cors:AllowedOrigins must list at least one origin.");
            return;
        }

        foreach (var origin in AllowedOrigins)
        {
            // A wildcard cannot be combined with credentials, and reflecting arbitrary
            // origins would let any page on the internet act as a signed-in user.
            if (origin is "*")
            {
                problems.Add(
                    $"{KoleksiOptions.SectionName}:Cors:AllowedOrigins must not contain '*' — "
                    + "the policy allows credentials.");
                continue;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                problems.Add($"{KoleksiOptions.SectionName}:Cors:AllowedOrigins contains a non-absolute origin.");
                continue;
            }

            // An origin is scheme + host + port. A trailing path silently never matches,
            // which presents as "CORS is broken" long after the deployment that caused it.
            if (!string.IsNullOrEmpty(uri.AbsolutePath.TrimEnd('/')) || !string.IsNullOrEmpty(uri.Query))
            {
                problems.Add(
                    $"{KoleksiOptions.SectionName}:Cors:AllowedOrigins entry '{origin}' must be a bare origin "
                    + "(scheme, host, and optional port only).");
            }
        }
    }
}
