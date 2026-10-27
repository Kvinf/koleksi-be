using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// An <see cref="IAmazonDynamoDB"/> that records the few calls an adapter makes and answers them with an empty,
/// successful response. Anything else throws, so a test learns at once if the code under test starts calling something
/// it was not expected to (a <c>Scan</c>, say).
/// </summary>
/// <remarks>
/// This proves the shape of what is sent. It cannot prove what DynamoDB does with it; that is the integration suite's job
/// (<c>AdminAuditLogTests</c>), which skips when DynamoDB Local is not running.
/// </remarks>
// Not sealed: DispatchProxy.Create generates a subclass of this type at run time, which the analyzer cannot see.
#pragma warning disable CA1852
internal class DynamoRecorder : DispatchProxy
{
    public List<TransactWriteItemsRequest> Transactions { get; } = [];

    public List<QueryRequest> Queries { get; } = [];

    public int Scans { get; private set; }

    /// <summary>Single-item and batched key reads (<c>GetItem</c>, <c>BatchGetItem</c>).</summary>
    public int PointReads { get; private set; }

    /// <summary>Every write of any kind, so a test can say "exactly one write, and it was a transaction".</summary>
    public int Writes { get; private set; }

    public static DynamoRecorder Create(out IAmazonDynamoDB client)
    {
        var proxy = Create<IAmazonDynamoDB, DynamoRecorder>();
        client = proxy;
        return (DynamoRecorder)(object)proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "TransactWriteItemsAsync":
                Transactions.Add((TransactWriteItemsRequest)args![0]!);
                Writes++;
                return Task.FromResult(new TransactWriteItemsResponse());

            case "QueryAsync":
                Queries.Add((QueryRequest)args![0]!);
                return Task.FromResult(new QueryResponse { Items = [] });

            case "GetItemAsync":
                PointReads++;
                return Task.FromResult(new GetItemResponse { Item = [] });

            case "BatchGetItemAsync":
                PointReads++;
                return Task.FromResult(new BatchGetItemResponse { Responses = [], UnprocessedKeys = [] });

            case "ScanAsync":
                Scans++;
                return Task.FromResult(new ScanResponse { Items = [] });

            case "Dispose":
                return null;

            default:
                throw new NotSupportedException($"The recorder does not expect a call to {targetMethod?.Name}.");
        }
    }
}
#pragma warning restore CA1852
