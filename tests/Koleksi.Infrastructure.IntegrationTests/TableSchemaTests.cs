using System.Text.Json;
using Amazon.DynamoDBv2;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// The table the tests create, against the table the stack deploys.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the storage tests are circular: they build a table from the same
/// constants the queries use, so they would pass even if <c>infra/lib/data-stack.ts</c>
/// declared something else entirely — and the first sign would be a
/// <c>ValidationException</c> in production naming an index that does not exist.
/// </para>
/// <para>
/// The comparison is against the synthesized CloudFormation template, which is the actual
/// deployment artefact rather than a restatement of it. Run
/// <c>npx cdk synth -c stage=dev</c> in <c>infra/</c> first; without it these skip.
/// </para>
/// </remarks>
public sealed class TableSchemaTests
{
    [CdkTemplateFact]
    public void Every_index_the_repositories_query_exists_in_the_deployed_table()
    {
        var deployed = DeployedTable();

        foreach (var (index, partitionKey, sortKey) in KoleksiTableSchema.Indexes)
        {
            deployed.Indexes.ShouldContainKey(
                index, $"The repositories query '{index}', which the stack does not declare.");

            var declared = deployed.Indexes[index];

            declared.PartitionKey.ShouldBe(
                partitionKey,
                $"'{index}' is queried on partition key '{partitionKey}' but the stack declares "
                + $"'{declared.PartitionKey}'.");

            declared.SortKey.ShouldBe(
                sortKey,
                $"'{index}' is queried on sort key '{sortKey}' but the stack declares "
                + $"'{declared.SortKey}'.");
        }
    }

    [CdkTemplateFact]
    public void The_deployed_table_declares_no_index_the_tests_do_not_cover()
    {
        // The other direction. An index in the stack that nothing here exercises is an
        // index whose keys nobody has ever checked against a real query.
        var deployed = DeployedTable();
        var covered = KoleksiTableSchema.Indexes.Select(index => index.Index).ToHashSet(StringComparer.Ordinal);

        deployed.Indexes.Keys.ShouldBeSubsetOf(covered);
    }

    [CdkTemplateFact]
    public void Every_index_projects_all_attributes()
    {
        // Load-bearing for gsi_auction_end above all: the close sweep rehydrates a whole
        // Auction from what that index returns, so a narrowed projection would hand back
        // auctions with a zeroed price and close them at it, with no error anywhere.
        var deployed = DeployedTable();

        foreach (var (index, declared) in deployed.Indexes)
        {
            declared.ProjectionType.ShouldBe(
                "ALL",
                $"'{index}' projects '{declared.ProjectionType}'. Every repository here rehydrates "
                + "full aggregates from index reads.");
        }
    }

    [CdkTemplateFact]
    public void The_primary_key_is_the_one_the_key_builders_compose()
    {
        var deployed = DeployedTable();

        deployed.PartitionKey.ShouldBe(DynamoTable.Pk);
        deployed.SortKey.ShouldBe(DynamoTable.Sk);
    }

    [CdkTemplateFact]
    public void Every_key_attribute_is_declared_as_a_string()
    {
        // Keys are composed as strings everywhere — padded prices, ISO instants, prefixed
        // ids. A numeric attribute definition would reject every one of them at write time.
        var deployed = DeployedTable();

        foreach (var (name, type) in deployed.AttributeTypes)
        {
            type.ShouldBe("S", $"Key attribute '{name}' is declared as '{type}'.");
        }
    }

    [CdkTemplateFact]
    public void Idempotency_claims_are_collected_by_the_attribute_the_store_writes()
    {
        // The claim row carries this attribute and nothing prunes the table otherwise, so
        // a mismatch here is unbounded growth that shows up as a bill rather than an error.
        var deployed = DeployedTable();

        deployed.TimeToLiveEnabled.ShouldBeTrue();
        deployed.TimeToLiveAttribute.ShouldBe(KoleksiTableSchema.TimeToLiveAttribute);
    }

    [CdkTemplateFact]
    public void The_table_is_billed_on_demand()
    {
        // Traffic is spiky by nature: an auction closing produces a burst that provisioned
        // capacity would either throttle or overpay all week to absorb.
        DeployedTable().BillingMode.ShouldBe("PAY_PER_REQUEST");
    }

    private static DeployedTableShape DeployedTable() =>
        CdkTemplate.ReadDynamoTable(CdkTemplate.Path!);
}

/// <summary>What the synthesized template says the table is.</summary>
internal sealed record DeployedTableShape(
    string PartitionKey,
    string SortKey,
    string BillingMode,
    bool TimeToLiveEnabled,
    string? TimeToLiveAttribute,
    IReadOnlyDictionary<string, string> AttributeTypes,
    IReadOnlyDictionary<string, DeployedIndexShape> Indexes);

/// <summary>What the synthesized template says one index is.</summary>
internal sealed record DeployedIndexShape(string PartitionKey, string SortKey, string ProjectionType);

/// <summary>Locates and parses the synthesized CDK template.</summary>
internal static class CdkTemplate
{
    /// <summary>
    /// Where the template can be, relative to each folder on the way up. The infra repo is a sibling of this one (<c>koleksi-infra</c>);
    /// <c>infra</c> is the name it had before the repositories were split. Looking only for the old name left every schema test
    /// skipped without anyone noticing. <c>KOLEKSI_INFRA_TEMPLATE</c> (an absolute path) overrides both.
    /// </summary>
    private static readonly string[] RelativePaths =
    [
        "koleksi-infra/cdk.out/Koleksi-Data-dev.template.json",
        "infra/cdk.out/Koleksi-Data-dev.template.json",
    ];

    public const string SkipReason =
        "The synthesized CDK template was not found. Run: cd koleksi-infra && npx cdk synth -c stage=dev (or set KOLEKSI_INFRA_TEMPLATE)";

    private static readonly Lazy<string?> Located = new(Locate, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string? Path => Located.Value;

    public static DeployedTableShape ReadDynamoTable(string templatePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(templatePath));

        var properties = document.RootElement
            .GetProperty("Resources")
            .EnumerateObject()
            .Select(resource => resource.Value)
            .Single(resource => resource.GetProperty("Type").GetString()
                is "AWS::DynamoDB::GlobalTable" or "AWS::DynamoDB::Table")
            .GetProperty("Properties");

        var attributeTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in properties.GetProperty("AttributeDefinitions").EnumerateArray())
        {
            attributeTypes[definition.GetProperty("AttributeName").GetString()!] =
                definition.GetProperty("AttributeType").GetString()!;
        }

        var indexes = new Dictionary<string, DeployedIndexShape>(StringComparer.Ordinal);
        if (properties.TryGetProperty("GlobalSecondaryIndexes", out var declaredIndexes))
        {
            foreach (var index in declaredIndexes.EnumerateArray())
            {
                var (partitionKey, sortKey) = KeysOf(index.GetProperty("KeySchema"));

                indexes[index.GetProperty("IndexName").GetString()!] = new DeployedIndexShape(
                    partitionKey,
                    sortKey,
                    index.GetProperty("Projection").GetProperty("ProjectionType").GetString()!);
            }
        }

        var (tablePartitionKey, tableSortKey) = KeysOf(properties.GetProperty("KeySchema"));

        var ttlEnabled = false;
        string? ttlAttribute = null;
        if (properties.TryGetProperty("TimeToLiveSpecification", out var ttl))
        {
            ttlEnabled = ttl.GetProperty("Enabled").GetBoolean();
            ttlAttribute = ttl.GetProperty("AttributeName").GetString();
        }

        return new DeployedTableShape(
            tablePartitionKey,
            tableSortKey,
            properties.TryGetProperty("BillingMode", out var billing)
                ? billing.GetString() ?? BillingMode.PROVISIONED.Value
                : BillingMode.PROVISIONED.Value,
            ttlEnabled,
            ttlAttribute,
            attributeTypes,
            indexes);
    }

    private static (string PartitionKey, string SortKey) KeysOf(JsonElement keySchema)
    {
        string partitionKey = string.Empty;
        string sortKey = string.Empty;

        foreach (var element in keySchema.EnumerateArray())
        {
            var name = element.GetProperty("AttributeName").GetString()!;
            if (element.GetProperty("KeyType").GetString() == "HASH")
            {
                partitionKey = name;
            }
            else
            {
                sortKey = name;
            }
        }

        return (partitionKey, sortKey);
    }

    /// <summary>
    /// Walks up from the test binary until the repository root comes into view.
    /// </summary>
    /// <remarks>
    /// The template lives outside the .NET solution, so there is no build-time link to it.
    /// Walking up is what keeps this working from a test explorer, from <c>dotnet test</c>,
    /// and from a different output path.
    /// </remarks>
    private static string? Locate()
    {
        if (Environment.GetEnvironmentVariable("KOLEKSI_INFRA_TEMPLATE") is { Length: > 0 } fixedPath)
        {
            return File.Exists(fixedPath) ? fixedPath : null;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            foreach (var relative in RelativePaths)
            {
                var candidate = System.IO.Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }
}

/// <summary>A fact that skips when the CDK app has not been synthesized.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CdkTemplateFactAttribute : FactAttribute
{
    public CdkTemplateFactAttribute()
    {
        if (CdkTemplate.Path is null)
        {
            Skip = CdkTemplate.SkipReason;
        }
    }
}
