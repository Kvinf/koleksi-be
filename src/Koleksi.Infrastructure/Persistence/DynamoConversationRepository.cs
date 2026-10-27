using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Messaging;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores conversations and their messages.
/// </summary>
/// <remarks>
/// <para>
/// A send is one transaction: the message row, and an <c>UpdateItem</c> on the summary row
/// that sets the latest-message fields and atomically adds one to the recipient's unread
/// counter. The summary is never written whole (<c>PutItem</c> would replace the counters
/// with whatever this process last read — CONTINUATION.md §11 has the same trap for listing
/// view counts), so two people replying in the same second cannot lose each other's counts.
/// </para>
/// <para>
/// The same <c>UpdateItem</c> creates the summary on a conversation's first message, using
/// <c>if_not_exists</c> for every field that must never change afterwards. That is what lets a
/// double-tapped "send" from two tabs land in one thread without a create-versus-update race.
/// </para>
/// </remarks>
internal sealed class DynamoConversationRepository(IAmazonDynamoDB dynamo, DynamoOptions options)
    : IConversationRepository
{
    public async Task<Conversation?> FindAsync(string conversationId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = options.TableName, Key = MetaKey(conversationId), ConsistentRead = true },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? ConversationItem.ToDomain(item) : null;
    }

    public async Task AppendAsync(Conversation conversation, ChatMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(message);

        var senderRole = conversation.RoleOf(message.SenderId);
        var recipientRole = senderRole == ConversationRole.Buyer ? ConversationRole.Seller : ConversationRole.Buyer;

        var lastAt = DynamoTable.FormatInstant(message.SentAt);
        var indexPrefix = DynamoTable.ConversationIndexPrefix();

        // Every attribute goes through a name placeholder: "id", "createdAt" and friends are
        // not the reserved words most likely to bite, but a placeholder is cheaper than
        // finding out which one is.
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["#entity"] = DynamoTable.EntityType,
            ["#id"] = ConversationItem.IdAttr,
            ["#listingId"] = ConversationItem.ListingIdAttr,
            ["#listingTitle"] = ConversationItem.ListingTitleAttr,
            ["#buyerId"] = ConversationItem.BuyerIdAttr,
            ["#buyerUsername"] = ConversationItem.BuyerUsernameAttr,
            ["#sellerId"] = ConversationItem.SellerIdAttr,
            ["#sellerUsername"] = ConversationItem.SellerUsernameAttr,
            ["#createdAt"] = ConversationItem.CreatedAtAttr,
            ["#lastAt"] = ConversationItem.LastMessageAtAttr,
            ["#preview"] = ConversationItem.LastPreviewAttr,
            ["#lastSender"] = ConversationItem.LastSenderIdAttr,
            ["#bpk"] = DynamoTable.BuyerPk,
            ["#bsk"] = DynamoTable.BuyerSk,
            ["#spk"] = DynamoTable.SellerPk,
            ["#ssk"] = DynamoTable.SellerSk,
            ["#senderUnread"] = ConversationItem.UnreadAttr(senderRole),
            ["#recipientUnread"] = ConversationItem.UnreadAttr(recipientRole),
        };

        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [":entity"] = Attr.S(ConversationItem.TypeName),
            [":id"] = Attr.S(conversation.Id),
            [":listingId"] = Attr.S(conversation.ListingId),
            [":listingTitle"] = Attr.S(conversation.ListingTitle),
            [":buyerId"] = Attr.S(conversation.BuyerId),
            [":buyerUsername"] = Attr.S(conversation.BuyerUsername),
            [":sellerId"] = Attr.S(conversation.SellerId),
            [":sellerUsername"] = Attr.S(conversation.SellerUsername),
            [":createdAt"] = Attr.Instant(conversation.CreatedAt),
            [":lastAt"] = Attr.S(lastAt),
            [":preview"] = Attr.S(conversation.LastMessagePreview),
            [":lastSender"] = Attr.S(message.SenderId),
            [":bpk"] = Attr.S(DynamoTable.BuyerPkValue(conversation.BuyerId)),
            [":spk"] = Attr.S(DynamoTable.SellerPkValue(conversation.SellerId)),
            [":bsk"] = Attr.S(DynamoTable.BuyerSkValue(indexPrefix, lastAt, conversation.Id)),
            [":ssk"] = Attr.S(DynamoTable.SellerSkValue(indexPrefix, lastAt, conversation.Id)),
            [":zero"] = Attr.N(0),
            [":one"] = Attr.N(1),
        };

        // Immutable once created: written only if absent. Everything after the blank line is
        // the part that moves on every message.
        var set = new List<string>
        {
            "#entity = if_not_exists(#entity, :entity)",
            "#id = if_not_exists(#id, :id)",
            "#listingId = if_not_exists(#listingId, :listingId)",
            "#listingTitle = if_not_exists(#listingTitle, :listingTitle)",
            "#buyerId = if_not_exists(#buyerId, :buyerId)",
            "#buyerUsername = if_not_exists(#buyerUsername, :buyerUsername)",
            "#sellerId = if_not_exists(#sellerId, :sellerId)",
            "#sellerUsername = if_not_exists(#sellerUsername, :sellerUsername)",
            "#createdAt = if_not_exists(#createdAt, :createdAt)",
            "#bpk = if_not_exists(#bpk, :bpk)",
            "#spk = if_not_exists(#spk, :spk)",
        };

        if (!string.IsNullOrEmpty(conversation.ListingPhotoKey))
        {
            names["#photo"] = ConversationItem.ListingPhotoKeyAttr;
            values[":photo"] = Attr.S(conversation.ListingPhotoKey);
            set.Add("#photo = if_not_exists(#photo, :photo)");
        }

        set.AddRange(
        [
            "#lastAt = :lastAt",
            "#preview = :preview",
            "#lastSender = :lastSender",
            "#bsk = :bsk",
            "#ssk = :ssk",
            "#senderUnread = :zero",
        ]);

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = ChatMessageItem.ToItem(message),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },
            new()
            {
                Update = new Update
                {
                    TableName = options.TableName,
                    Key = MetaKey(conversation.Id),
                    UpdateExpression = $"SET {string.Join(", ", set)} ADD #recipientUnread :one",
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values,
                },
            },
        };

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled)
            when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            throw new ConcurrencyConflictException($"A message already exists with id '{message.Id}'.");
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(
        string conversationId, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // Newest-first so Limit keeps the end of the thread, then flipped back to the order a
        // conversation is read in. Strongly consistent: the caller usually reads straight after
        // sending, and an eventually consistent read could leave their own message out.
        var latest = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                KeyConditionExpression = $"{DynamoTable.Pk} = :pk AND begins_with({DynamoTable.Sk}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.ConversationPk(conversationId)),
                    [":prefix"] = Attr.S(DynamoTable.MessageSkPrefix()),
                },
                ScanIndexForward = false,
                ConsistentRead = true,
                Limit = limit,
            },
            ct).ConfigureAwait(false);

        var page = (latest.Items ?? []).Select(ChatMessageItem.ToDomain).ToList();
        page.Reverse();
        return page;
    }

    public async Task MarkReadAsync(string conversationId, ConversationRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        try
        {
            await dynamo.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = options.TableName,
                    Key = MetaKey(conversationId),
                    UpdateExpression = "SET #unread = :zero",
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk})",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#unread"] = ConversationItem.UnreadAttr(role),
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":zero"] = Attr.N(0),
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // Never created, so there is nothing to mark. Not an error worth surfacing.
        }
    }

    public async Task<IReadOnlyList<Conversation>> ListForUserAsync(string userId, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // A person is a buyer in some threads and a seller in others, and the two sides live
        // in two indexes. Two bounded queries in flight together beat one scan, and a person
        // cannot be both sides of the same conversation, so nothing needs de-duplicating.
        var asBuyer = QueryIndexAsync(
            DynamoTable.BuyerIndex, DynamoTable.BuyerPk, DynamoTable.BuyerPkValue(userId),
            DynamoTable.BuyerSk, limit, ct);
        var asSeller = QueryIndexAsync(
            DynamoTable.SellerIndex, DynamoTable.SellerPk, DynamoTable.SellerPkValue(userId),
            DynamoTable.SellerSk, limit, ct);

        var both = await Task.WhenAll(asBuyer, asSeller).ConfigureAwait(false);

        return both
            .SelectMany(list => list)
            .OrderByDescending(conversation => conversation.LastMessageAt)
            .Take(limit)
            .ToList();
    }

    private async Task<List<Conversation>> QueryIndexAsync(
        string indexName, string pkAttribute, string pkValue, string skAttribute, int limit, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                IndexName = indexName,
                KeyConditionExpression = $"{pkAttribute} = :pk AND begins_with({skAttribute}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(pkValue),
                    [":prefix"] = Attr.S(DynamoTable.ConversationIndexPrefix()),
                },
                ScanIndexForward = false,
                Limit = limit,
            },
            ct).ConfigureAwait(false);

        return (response.Items ?? []).Select(ConversationItem.ToDomain).ToList();
    }

    private static Dictionary<string, AttributeValue> MetaKey(string conversationId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ConversationPk(conversationId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.ConversationMetaSk()),
        };
}
