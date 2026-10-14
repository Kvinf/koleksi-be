using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPaymentOrphanStore"/> as one small row per orphaned payment, kept for ever.
/// </summary>
/// <remarks>
/// Written by a conditional put on (order number, processor reference), so the gateway redelivering
/// the same notification records nothing new and does not move the original <c>receivedAt</c>.
/// No TTL: this is the evidence a refund is made from. Idle cost is zero (rows in the existing
/// on-demand table, written only when something has already gone wrong).
/// </remarks>
internal sealed class DynamoPaymentOrphanStore(IAmazonDynamoDB dynamo, DynamoOptions options) : IPaymentOrphanStore
{
    public const string TypeName = "PaymentOrphan";

    public async Task RecordAsync(PaymentOrphan orphan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(orphan);

        // The processor reference identifies the payment; the event id stands in when the gateway sent none.
        var paymentKey = string.IsNullOrWhiteSpace(orphan.ProcessorReference) ? orphan.EventId : orphan.ProcessorReference;

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.PaymentOrphanPk(orphan.OrderNumber)),
                        [DynamoTable.Sk] = Attr.S("#" + paymentKey),
                        [DynamoTable.EntityType] = Attr.S(TypeName),
                        ["orderNumber"] = Attr.S(orphan.OrderNumber),
                        ["orderId"] = Attr.SOrNull(orphan.OrderId),
                        ["amount"] = Attr.MoneyValue(orphan.Amount),
                        ["processorReference"] = Attr.SOrNull(orphan.ProcessorReference),
                        ["eventId"] = Attr.S(orphan.EventId),
                        ["reason"] = Attr.S(orphan.Reason),
                        ["receivedAt"] = Attr.Instant(orphan.ReceivedAt),
                    },
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // The same payment was already recorded by an earlier delivery.
        }
    }
}
