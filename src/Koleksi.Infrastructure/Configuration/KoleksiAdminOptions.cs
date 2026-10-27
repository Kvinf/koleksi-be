namespace Koleksi.Infrastructure;

/// <summary>
/// Everything the admin API needs from its environment. A separate section (<c>KoleksiAdmin</c>) from the
/// public API's <c>Koleksi</c> on purpose, so the admin Lambda cannot be started with the public user pool, bucket
/// names or payment configuration by mistake, and the public API cannot be started with the staff pool.
/// </summary>
/// <remarks>
/// <see cref="Validate"/> runs before the host is built and reports every problem at once, exactly like
/// <see cref="KoleksiOptions.Validate(bool)"/>. A deployed stage must name its staff pool and a shared secret; only a
/// developer's own machine may leave them out.
/// </remarks>
public sealed class KoleksiAdminOptions
{
    public const string SectionName = "KoleksiAdmin";

    public string Region { get; init; } = string.Empty;

    /// <summary>The deployment stage, set by CDK on every deployed stage. Empty locally.</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>
    /// The staff user pool and its app client. Tokens from any other pool, or from another client of this pool, are refused.
    /// </summary>
    public CognitoOptions StaffPool { get; init; } = new();

    public DynamoOptions Dynamo { get; init; } = new();

    /// <summary>
    /// The shared secret the admin web tier sends as <c>X-Koleksi-Admin-Internal</c>. Required on a deployed stage: with
    /// it the API answers only the web tier, so a stolen staff token used from anywhere else still gets nothing.
    /// </summary>
    public SecurityOptions Security { get; init; } = new();

    /// <summary>
    /// The development stand-ins. Today only the refund sender: with this on, a refund is "simulated" instead of "pending gateway".
    /// Neither moves money. Refused outside a developer's machine and the dev stage, exactly like the public API's simulators.
    /// </summary>
    public SimulatorOptions Simulators { get; init; } = new();

    /// <summary>Whether the host is a deployed stage (a stage name is set), as opposed to a developer's machine.</summary>
    public bool IsDeployed => !string.IsNullOrWhiteSpace(Stage);

    /// <summary>
    /// Fails fast on a configuration that cannot serve staff safely.
    /// </summary>
    /// <param name="allowLocalEndpoints">True only for a developer's machine (the Development environment).</param>
    /// <exception cref="InvalidOperationException">Any required value is missing or malformed.</exception>
    public void Validate(bool allowLocalEndpoints)
    {
        var problems = new List<string>();

        StaffPool.ResolvedRegion = string.IsNullOrWhiteSpace(StaffPool.Region) ? Region : StaffPool.Region;
        StaffPool.Validate(problems);
        Dynamo.Validate(problems, allowLocalEndpoints);
        Security.Validate(problems);
        Simulators.Validate(problems, Stage, allowLocalEndpoints);

        if (!allowLocalEndpoints)
        {
            if (!Security.HasInternalSecret)
            {
                problems.Add($"{SectionName}:Security:InternalSecret is required outside a developer's machine.");
            }

            if (!IsDeployed)
            {
                problems.Add($"{SectionName}:Stage is required outside a developer's machine.");
            }
        }

        if (problems.Count > 0)
        {
            // The shared validators name their keys under the public API's section. Say the keys this API reads.
            var named = problems.Select(problem => problem
                .Replace($"{KoleksiOptions.SectionName}:Cognito:", $"{SectionName}:StaffPool:", StringComparison.Ordinal)
                .Replace($"{KoleksiOptions.SectionName}:", $"{SectionName}:", StringComparison.Ordinal));

            throw new InvalidOperationException(
                $"Configuration section '{SectionName}' is invalid:{Environment.NewLine}  - "
                + string.Join($"{Environment.NewLine}  - ", named)
                + $"{Environment.NewLine}See docs/configuration.md.");
        }
    }
}
