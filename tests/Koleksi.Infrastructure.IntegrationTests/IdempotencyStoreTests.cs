using Amazon.DynamoDBv2.Model;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// <c>DynamoIdempotencyStore</c> against a real table.
/// </summary>
/// <remarks>
/// Two duplicate sources matter here. Payment webhooks are delivered at least once by
/// design, and a buyer on a flaky connection retries a checkout they cannot tell
/// succeeded. Neither may produce two charges or two orders — and the only thing standing
/// between them and that is a conditional write, which is exactly the part a fake cannot
/// vouch for.
/// </remarks>
public sealed class IdempotencyStoreTests : DynamoTestBase
{
    private const string Scope = "checkout:b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33";

    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    [DynamoLocalFact]
    public async Task A_key_can_be_claimed_once()
    {
        var key = Guid.NewGuid().ToString("N");

        (await Table.Idempotency.TryClaimAsync(Scope, key, Retention, Ct)).ShouldBeTrue();
        (await Table.Idempotency.TryClaimAsync(Scope, key, Retention, Ct)).ShouldBeFalse();
    }

    [DynamoLocalFact]
    public async Task Concurrent_claims_of_one_key_produce_exactly_one_winner()
    {
        // The race this type exists for. A read-then-write would let several of these
        // observe "unclaimed" and all proceed, which is two charges for one checkout.
        var key = Guid.NewGuid().ToString("N");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Table.Idempotency.TryClaimAsync(Scope, key, Retention, Ct)));

        attempts.Count(claimed => claimed).ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task A_recorded_result_is_replayed_to_the_duplicate()
    {
        var key = Guid.NewGuid().ToString("N");
        const string result = """{"orderNumber":"KLK-7F3A21"}""";

        (await Table.Idempotency.TryClaimAsync(Scope, key, Retention, Ct)).ShouldBeTrue();
        await Table.Idempotency.RecordResultAsync(Scope, key, result, Ct);

        (await Table.Idempotency.GetRecordedResultAsync(Scope, key, Ct)).ShouldBe(result);
    }

    [DynamoLocalFact]
    public async Task A_claim_with_no_result_yet_replays_nothing()
    {
        var key = Guid.NewGuid().ToString("N");
        await Table.Idempotency.TryClaimAsync(Scope, key, Retention, Ct);

        (await Table.Idempotency.GetRecordedResultAsync(Scope, key, Ct)).ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_result_cannot_be_recorded_against_a_key_nobody_claimed()
    {
        // Otherwise an unclaimed key could be seeded with a result of the caller's
        // choosing, and the next genuine request would replay it instead of doing the work.
        var key = Guid.NewGuid().ToString("N");

        await Should.ThrowAsync<ConditionalCheckFailedException>(
            () => Table.Idempotency.RecordResultAsync(Scope, key, "{}", Ct));
    }

    [DynamoLocalFact]
    public async Task One_callers_key_cannot_collide_with_anothers()
    {
        // The scope is part of the partition key. A global namespace would let anyone
        // suppress someone else's checkout by guessing the key it used.
        const string sharedKey = "retry-1";
        const string otherScope = "checkout:9c2e5a71-08bd-4f36-b7aa-1e4c9d05f882";

        (await Table.Idempotency.TryClaimAsync(Scope, sharedKey, Retention, Ct)).ShouldBeTrue();
        (await Table.Idempotency.TryClaimAsync(otherScope, sharedKey, Retention, Ct)).ShouldBeTrue();

        await Table.Idempotency.RecordResultAsync(Scope, sharedKey, "mine", Ct);

        (await Table.Idempotency.GetRecordedResultAsync(otherScope, sharedKey, Ct)).ShouldBeNull();
    }

    [DynamoLocalTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31 * 24)]
    public async Task An_unbounded_retention_is_refused(int hours)
    {
        // Without a ceiling, a retention of a hundred years is one request away and the
        // row is never collected.
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => Table.Idempotency.TryClaimAsync(
                Scope, Guid.NewGuid().ToString("N"), TimeSpan.FromHours(hours), Ct));
    }

    [DynamoLocalFact]
    public async Task A_claim_carries_the_expiry_attribute_dynamodb_collects_by()
    {
        // TTL is what keeps the table from growing without bound, and it only works on an
        // attribute holding Unix *seconds* — any other unit silently never expires.
        var key = Guid.NewGuid().ToString("N");
        await Table.Idempotency.TryClaimAsync(Scope, key, Retention, Ct);

        var stored = await Table.Client.GetItemAsync(new GetItemRequest
        {
            TableName = Table.TableName,
            Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [DynamoTable.Pk] = new AttributeValue { S = DynamoTable.IdempotencyPkValue(Scope, key) },
                [DynamoTable.Sk] = new AttributeValue { S = "#CLAIM" },
            },
            ConsistentRead = true,
        });

        var expiresAt = stored.Item[KoleksiTableSchema.TimeToLiveAttribute].N;
        expiresAt.ShouldNotBeNull();

        long.Parse(expiresAt, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe((Table.Clock.UtcNow + Retention).ToUnixTimeSeconds());
    }
}
