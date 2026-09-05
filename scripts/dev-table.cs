#:package AWSSDK.DynamoDBv2@4.0.103.1

// Creates the local development table in DynamoDB Local.
//
//   dotnet run scripts/dev-table.cs
//
// The shape is read from the synthesized CloudFormation template rather than declared
// here, so there is exactly one description of the table in the repository and a local
// table is provably the shape that gets deployed. Run "npx cdk synth -c stage=dev" in
// the koleksi-infra repo first; this script says so if the template is missing.
//
// koleksi-infra is a separate repo, expected checked out as a sibling of this one (e.g.
// both under the same parent directory as ../koleksi-infra). Set KOLEKSI_INFRA_TEMPLATE
// to an absolute path instead if your checkout isn't laid out that way.
//
// Safe to re-run: an existing table is left alone rather than replaced, because dropping
// it would silently discard whatever a developer had just been testing with.

using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;

const string ServiceUrl = "http://localhost:8000";
const string RelativeTemplatePath = "../koleksi-infra/cdk.out/Koleksi-Data-dev.template.json";

var tableName = args.Length > 0 ? args[0] : "koleksi-dev";

var templatePath = Environment.GetEnvironmentVariable("KOLEKSI_INFRA_TEMPLATE")
    ?? FindSiblingTemplate();

if (templatePath is null)
{
    Console.Error.WriteLine($"Could not find {RelativeTemplatePath}. Run this from a checkout with");
    Console.Error.WriteLine("koleksi-infra as a sibling directory, after:");
    Console.Error.WriteLine("  cd ../koleksi-infra && npx cdk synth -c stage=dev");
    Console.Error.WriteLine("or set KOLEKSI_INFRA_TEMPLATE to the template's absolute path.");
    return 1;
}

var template = JsonDocument.Parse(File.ReadAllText(templatePath));

var properties = template.RootElement
    .GetProperty("Resources")
    .EnumerateObject()
    .Select(resource => resource.Value)
    .Single(resource => resource.GetProperty("Type").GetString()
        is "AWS::DynamoDB::GlobalTable" or "AWS::DynamoDB::Table")
    .GetProperty("Properties");

// DynamoDB Local never checks that a key exists, but since version 3 it does check that it
// is shaped like one. These are AWS's own documentation placeholders.
using var dynamo = new AmazonDynamoDBClient(
    new BasicAWSCredentials("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"),
    new AmazonDynamoDBConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "ap-southeast-3" });

try
{
    await dynamo.DescribeTableAsync(tableName);
    Console.WriteLine($"'{tableName}' already exists at {ServiceUrl}. Nothing to do.");
    return 0;
}
catch (ResourceNotFoundException)
{
    // The expected path on a first run.
}
catch (AmazonServiceException failure) when (failure.StatusCode == 0)
{
    Console.Error.WriteLine($"Nothing is listening at {ServiceUrl}. Start DynamoDB Local with:");
    Console.Error.WriteLine("  docker run -d --name koleksi-dynamodb-local -p 8000:8000 \\");
    Console.Error.WriteLine("    amazon/dynamodb-local:latest -jar DynamoDBLocal.jar -inMemory -sharedDb");
    return 1;
}

await dynamo.CreateTableAsync(new CreateTableRequest
{
    TableName = tableName,
    KeySchema = ReadKeySchema(properties.GetProperty("KeySchema")),
    AttributeDefinitions = [.. properties.GetProperty("AttributeDefinitions").EnumerateArray()
        .Select(definition => new AttributeDefinition
        {
            AttributeName = definition.GetProperty("AttributeName").GetString(),
            AttributeType = definition.GetProperty("AttributeType").GetString(),
        })],
    BillingMode = BillingMode.PAY_PER_REQUEST,
    GlobalSecondaryIndexes = [.. properties.GetProperty("GlobalSecondaryIndexes").EnumerateArray()
        .Select(index => new GlobalSecondaryIndex
        {
            IndexName = index.GetProperty("IndexName").GetString(),
            KeySchema = ReadKeySchema(index.GetProperty("KeySchema")),
            Projection = new Projection
            {
                ProjectionType = index.GetProperty("Projection").GetProperty("ProjectionType").GetString(),
            },
        })],
});

// Indexes backfill asynchronously. A query against one that is still building returns an
// empty page rather than an error, which reads as "the seed did not work".
for (var attempt = 0; attempt < 50; attempt++)
{
    var described = await dynamo.DescribeTableAsync(tableName);
    if (described.Table.TableStatus == TableStatus.ACTIVE
        && described.Table.GlobalSecondaryIndexes.TrueForAll(index => index.IndexStatus == IndexStatus.ACTIVE))
    {
        break;
    }

    await Task.Delay(TimeSpan.FromMilliseconds(100));
}

if (properties.TryGetProperty("TimeToLiveSpecification", out var ttl) && ttl.GetProperty("Enabled").GetBoolean())
{
    await dynamo.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
    {
        TableName = tableName,
        TimeToLiveSpecification = new TimeToLiveSpecification
        {
            Enabled = true,
            AttributeName = ttl.GetProperty("AttributeName").GetString(),
        },
    });
}

var indexNames = properties.GetProperty("GlobalSecondaryIndexes").EnumerateArray()
    .Select(index => index.GetProperty("IndexName").GetString());

Console.WriteLine($"Created '{tableName}' at {ServiceUrl} with indexes: {string.Join(", ", indexNames)}");
return 0;

static List<KeySchemaElement> ReadKeySchema(JsonElement keySchema) =>
    [.. keySchema.EnumerateArray().Select(element => new KeySchemaElement
    {
        AttributeName = element.GetProperty("AttributeName").GetString(),
        KeyType = element.GetProperty("KeyType").GetString(),
    })];

static string? FindSiblingTemplate()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, RelativeTemplatePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    return null;
}
