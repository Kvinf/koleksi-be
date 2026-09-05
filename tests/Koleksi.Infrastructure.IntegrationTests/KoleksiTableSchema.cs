using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Infrastructure.Persistence;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// The table these tests run against, built from the same constants the repositories query.
/// </summary>
/// <remarks>
/// <para>
/// This has to be the same table <c>infra/lib/data-stack.ts</c> declares, or the tests
/// prove nothing about production. Two things keep it honest: every name here comes from
/// <see cref="DynamoTable"/>, which is also where the queries get theirs, and
/// <c>TableSchemaTests</c> compares this definition against the synthesized CloudFormation
/// template attribute by attribute.
/// </para>
/// <para>
/// Every projection is <c>ALL</c>, matching the stack. That is load-bearing for
/// <c>gsi_auction_end</c> in particular: the close sweep rehydrates a complete
/// <c>Auction</c> from what the index returns, so a narrowed projection would hand back
/// auctions with a zeroed price and no error at all.
/// </para>
/// </remarks>
internal static class KoleksiTableSchema
{
    /// <summary>The attribute DynamoDB's TTL collects idempotency claims by.</summary>
    public const string TimeToLiveAttribute = "expiresAt";

    /// <summary>Every index, in the order the stack declares them.</summary>
    public static IReadOnlyList<(string Index, string PartitionKey, string SortKey)> Indexes { get; } =
    [
        (DynamoTable.SellerIndex, DynamoTable.SellerPk, DynamoTable.SellerSk),
        (DynamoTable.BrowseNewestIndex, DynamoTable.BrowseNewestPk, DynamoTable.BrowseNewestSk),
        (DynamoTable.BrowsePriceIndex, DynamoTable.BrowsePricePk, DynamoTable.BrowsePriceSk),
        (DynamoTable.BrowseEndingIndex, DynamoTable.BrowseEndingPk, DynamoTable.BrowseEndingSk),
        (DynamoTable.AuctionEndIndex, DynamoTable.AuctionEndPk, DynamoTable.AuctionEndSk),
        (DynamoTable.BuyerIndex, DynamoTable.BuyerPk, DynamoTable.BuyerSk),
    ];

    public static CreateTableRequest CreateRequest(string tableName)
    {
        var attributes = new List<AttributeDefinition>
        {
            Definition(DynamoTable.Pk),
            Definition(DynamoTable.Sk),
        };

        var indexes = new List<GlobalSecondaryIndex>(Indexes.Count);

        foreach (var (index, partitionKey, sortKey) in Indexes)
        {
            attributes.Add(Definition(partitionKey));
            attributes.Add(Definition(sortKey));

            indexes.Add(new GlobalSecondaryIndex
            {
                IndexName = index,
                KeySchema =
                [
                    new KeySchemaElement { AttributeName = partitionKey, KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = sortKey, KeyType = KeyType.RANGE },
                ],
                Projection = new Projection { ProjectionType = ProjectionType.ALL },
            });
        }

        return new CreateTableRequest
        {
            TableName = tableName,
            KeySchema =
            [
                new KeySchemaElement { AttributeName = DynamoTable.Pk, KeyType = KeyType.HASH },
                new KeySchemaElement { AttributeName = DynamoTable.Sk, KeyType = KeyType.RANGE },
            ],
            AttributeDefinitions = attributes,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes = indexes,
        };
    }

    private static AttributeDefinition Definition(string name) =>
        new() { AttributeName = name, AttributeType = ScalarAttributeType.S };
}
