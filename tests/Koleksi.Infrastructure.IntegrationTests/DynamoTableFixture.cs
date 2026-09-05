using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// A table of its own for one test class, and the real repositories wired to it.
/// </summary>
/// <remarks>
/// <para>
/// One table per class rather than one per run. Facet counters are per-category and
/// shared, so two classes indexing listings into <c>cards</c> at the same time would read
/// each other's totals — and a test that fails only when run alongside another is worse
/// than no test.
/// </para>
/// <para>
/// The repositories here are the production types, resolved with the production category
/// seed. Nothing is subclassed and nothing is stubbed: the whole point is to find out what
/// DynamoDB does with the requests they actually build.
/// </para>
/// </remarks>
public sealed class DynamoTableFixture : IAsyncLifetime
{
    private AmazonDynamoDBClient? _client;

    public string TableName { get; } = $"koleksi-test-{Guid.NewGuid():N}";

    public AmazonDynamoDBClient Client => _client
        ?? throw new InvalidOperationException("DynamoDB Local was not available for this run.");

    public FixedClock Clock { get; } = new();

    public IListingRepository Listings { get; private set; } = null!;

    public IAuctionRepository Auctions { get; private set; } = null!;

    public IOfferRepository Offers { get; private set; } = null!;

    public IOrderRepository Orders { get; private set; } = null!;

    public IListingSearchIndex SearchIndex { get; private set; } = null!;

    public IIdempotencyStore Idempotency { get; private set; } = null!;

    public ICategoryRepository Categories { get; } = new SeededCategoryRepository();

    public async Task InitializeAsync()
    {
        if (!DynamoLocal.IsAvailable)
        {
            // Every test in the class is already marked skipped by its attribute, so there
            // is nothing to set up and nothing to fail.
            return;
        }

        _client = DynamoLocal.CreateClient();

        await _client.CreateTableAsync(KoleksiTableSchema.CreateRequest(TableName));
        await WaitUntilActiveAsync();

        await _client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = TableName,
            TimeToLiveSpecification = new TimeToLiveSpecification
            {
                Enabled = true,
                AttributeName = KoleksiTableSchema.TimeToLiveAttribute,
            },
        });

        var options = new DynamoOptions { TableName = TableName, ServiceUrl = DynamoLocal.ServiceUrl };

        Listings = new DynamoListingRepository(_client, options);
        Auctions = new DynamoAuctionRepository(_client, options);
        Offers = new DynamoOfferRepository(_client, options);
        Orders = new DynamoOrderRepository(_client, options);
        Idempotency = new DynamoIdempotencyStore(_client, options, Clock);
        SearchIndex = new DynamoListingSearchIndex(
            _client, Categories, options, NullLogger<DynamoListingSearchIndex>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            await _client.DeleteTableAsync(TableName);
        }
        catch (ResourceNotFoundException)
        {
            // Already gone. Nothing to clean up, and failing teardown would mask whatever
            // the test itself found.
        }

        _client.Dispose();
    }

    /// <summary>
    /// Waits for the table and every index to become usable.
    /// </summary>
    /// <remarks>
    /// DynamoDB Local creates in milliseconds, but a query against an index that is still
    /// backfilling returns an empty page rather than an error — which would present as a
    /// flaky test blaming the repository.
    /// </remarks>
    private async Task WaitUntilActiveAsync()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var described = await Client.DescribeTableAsync(TableName);
            var table = described.Table;

            var ready = table.TableStatus == TableStatus.ACTIVE
                && (table.GlobalSecondaryIndexes ?? []).TrueForAll(
                    index => index.IndexStatus == IndexStatus.ACTIVE);

            if (ready)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new InvalidOperationException($"Table '{TableName}' did not become active.");
    }
}

/// <summary>A clock the tests set, so nothing depends on when the suite happens to run.</summary>
public sealed class FixedClock : IClock
{
    public static readonly DateTimeOffset Origin = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; } = Origin;
}
