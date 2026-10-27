using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IShippingClaimStore"/> as one small row per proof photo and per waybill, written in one transaction.
/// </summary>
/// <remarks>
/// Each row is a conditional put that succeeds when the value is unclaimed <b>or already claimed by this same
/// order</b>, so a retry of one shipment passes and a second order is refused. The waybill row is the first write
/// of the transaction and the photos follow, which is how a cancelled transaction is traced back to what clashed.
/// Idle cost is zero: rows in the existing on-demand table, written only when an order is shipped.
/// </remarks>
internal sealed class DynamoShippingClaimStore(IAmazonDynamoDB dynamo, DynamoOptions options) : IShippingClaimStore
{
    public const string TypeName = "ShippingClaim";

    private const string ProofKind = "PROOF";
    private const string TrackingKind = "TRACK";
    private const string ClaimSk = "#CLAIM";

    public async Task<ShippingClaimConflict?> TryClaimAsync(
        string orderId, string courierCode, string trackingNumber, IReadOnlyList<string> proofKeys, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(courierCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackingNumber);
        ArgumentNullException.ThrowIfNull(proofKeys);

        // A waybill is only unique within its courier.
        var writes = new List<TransactWriteItem>(proofKeys.Count + 1)
        {
            Claim(DynamoTable.ShippingClaimPk(TrackingKind, $"{courierCode}#{trackingNumber}"), orderId),
        };

        foreach (var key in proofKeys.Distinct(StringComparer.Ordinal))
        {
            writes.Add(Claim(DynamoTable.ShippingClaimPk(ProofKind, key), orderId));
        }

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
            return null;
        }
        catch (TransactionCanceledException cancelled)
        {
            // A write that met another writer on the same row is retried by the caller, not reported as a clash.
            if (DynamoTransactionConflicts.IsTransactionConflict(cancelled))
            {
                throw new ConcurrencyConflictException("The shipment's proof claims are being written by another request.");
            }

            var reasons = cancelled.CancellationReasons;
            for (var index = 0; index < writes.Count && reasons is not null && index < reasons.Count; index++)
            {
                if (string.Equals(reasons[index].Code, DynamoTransactionConflicts.ConditionalCheckFailed, StringComparison.Ordinal))
                {
                    return index == 0 ? ShippingClaimConflict.TrackingReused : ShippingClaimConflict.ProofReused;
                }
            }

            throw;
        }
    }

    private TransactWriteItem Claim(string pk, string orderId) => new()
    {
        Put = new Put
        {
            TableName = options.TableName,
            Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [DynamoTable.Pk] = Attr.S(pk),
                [DynamoTable.Sk] = Attr.S(ClaimSk),
                [DynamoTable.EntityType] = Attr.S(TypeName),
                ["orderId"] = Attr.S(orderId),
            },
            ConditionExpression = $"attribute_not_exists({DynamoTable.Pk}) OR orderId = :order",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":order"] = Attr.S(orderId),
            },
        },
    };
}
