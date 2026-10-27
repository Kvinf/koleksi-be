namespace Koleksi.Infrastructure;

/// <summary>Trust between the web tier and this API.</summary>
public sealed class SecurityOptions
{
    /// <summary>Shortest secret accepted. Short enough to paste, long enough that guessing it is hopeless.</summary>
    public const int MinInternalSecretLength = 32;

    /// <summary>
    /// A shared secret the web tier sends as <c>X-Koleksi-Internal</c> to prove a request really
    /// comes from it, which is what lets the API believe the <c>X-Koleksi-Client-Ip</c> it carries.
    /// </summary>
    /// <remarks>
    /// Optional; unset turns the feature off and the client-IP header is never read. Without it,
    /// every visitor reaches this API through the web tier's own egress address, so the
    /// address-keyed rate limits (sign-in above all) would be one shared bucket for everyone.
    /// Supplied by the CDK app as an environment variable, exactly as secret as the origin-verify
    /// header the same stack already passes that way.
    /// </remarks>
    public string? InternalSecret { get; init; }

    /// <summary>Whether the trusted-client-address feature is switched on.</summary>
    public bool HasInternalSecret => !string.IsNullOrWhiteSpace(InternalSecret);

    internal void Validate(List<string> problems)
    {
        // Never echoes the value: a too-short secret is still a secret.
        if (HasInternalSecret && InternalSecret!.Length < MinInternalSecretLength)
        {
            problems.Add(
                $"{KoleksiOptions.SectionName}:Security:InternalSecret must be at least "
                + $"{MinInternalSecretLength} characters when set.");
        }
    }
}
