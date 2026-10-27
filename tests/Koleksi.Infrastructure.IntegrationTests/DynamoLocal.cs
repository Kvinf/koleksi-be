using Amazon.DynamoDBv2;
using Amazon.Runtime;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// Reaching the local DynamoDB these tests run against.
/// </summary>
/// <remarks>
/// <para>
/// Start it with:
/// </para>
/// <code>
/// docker run -d --name koleksi-dynamodb-local -p 8000:8000 \
///   amazon/dynamodb-local:latest -jar DynamoDBLocal.jar -inMemory -sharedDb
/// </code>
/// <para>
/// When it is not reachable these tests <b>skip</b> rather than fail. That is deliberate:
/// the rest of the suite runs with nothing installed, so a developer without Docker still
/// gets a green build and a visible "skipped" count telling them what they did not run.
/// A suite that fails without a container is a suite people learn to ignore.
/// </para>
/// </remarks>
internal static class DynamoLocal
{
    /// <summary>
    /// Where DynamoDB Local listens. Port 8000 unless <c>KOLEKSI_TEST_DYNAMODB_URL</c> says otherwise, so a run can use a throwaway container
    /// (a long-lived one gets slower with every run) without touching the one that holds a developer's local table.
    /// </summary>
    public static readonly string ServiceUrl =
        Environment.GetEnvironmentVariable("KOLEKSI_TEST_DYNAMODB_URL") is { Length: > 0 } url ? url : "http://localhost:8000";

    public static readonly string SkipReason =
        "DynamoDB Local is not reachable at " + ServiceUrl + ". Start it with: docker run -d "
        + "--name koleksi-dynamodb-local -p 8000:8000 amazon/dynamodb-local:latest "
        + "-jar DynamoDBLocal.jar -inMemory -sharedDb";

    private static readonly Lazy<bool> Probe = new(TryReach, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Probe.Value;

    /// <summary>
    /// A client pointed at the local endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Credentials are supplied explicitly rather than left to the SDK's fallback chain. A
    /// test that quietly picks up a developer's real credentials is a test one typo in a
    /// service URL away from writing to a real table.
    /// </para>
    /// <para>
    /// These are AWS's own documentation placeholders. DynamoDB Local does not check that
    /// a key exists, but since version 3 it does check that it is <i>shaped</i> like one,
    /// and answers "the Access Key ID or security token is invalid" to anything else.
    /// </para>
    /// </remarks>
    public static AmazonDynamoDBClient CreateClient() => new(
        new BasicAWSCredentials("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"),
        new AmazonDynamoDBConfig
        {
            ServiceURL = ServiceUrl,
            AuthenticationRegion = "ap-southeast-3",

            // Fail fast. The point of the probe is to decide in a second or two whether to
            // skip, not to spend the SDK's default retry budget discovering nothing is
            // listening.
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromSeconds(5),
        });

    /// <summary>
    /// Decides whether the endpoint is absent, as opposed to present and unhappy.
    /// </summary>
    /// <remarks>
    /// Only a failure to connect counts as "not running". If DynamoDB Local answers at all
    /// — with a credential error, a permissions error, anything — it is reachable and the
    /// problem is ours, so the exception is allowed out. Swallowing it would turn a
    /// one-character configuration mistake into forty-nine tests that silently stop
    /// running, which is the precise failure this suite is meant to avoid.
    /// </remarks>
    private static bool TryReach()
    {
        try
        {
            using var client = CreateClient();
            client.ListTablesAsync(limit: 1).GetAwaiter().GetResult();
            return true;
        }
        catch (AmazonServiceException failure) when (failure.StatusCode == 0)
        {
            // No HTTP status: the request never reached a server.
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}

/// <summary>A fact that skips when DynamoDB Local is not running.</summary>
/// <remarks>
/// The skip is decided during discovery, which is why this is an attribute rather than a
/// guard inside each test: xUnit 2 reads <see cref="FactAttribute.Skip"/> when it enumerates
/// tests, so a skipped test is reported as skipped instead of quietly passing on an early
/// return.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DynamoLocalFactAttribute : FactAttribute
{
    public DynamoLocalFactAttribute()
    {
        if (!DynamoLocal.IsAvailable)
        {
            Skip = DynamoLocal.SkipReason;
        }
    }
}

/// <summary>A theory that skips when DynamoDB Local is not running.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DynamoLocalTheoryAttribute : TheoryAttribute
{
    public DynamoLocalTheoryAttribute()
    {
        if (!DynamoLocal.IsAvailable)
        {
            Skip = DynamoLocal.SkipReason;
        }
    }
}
