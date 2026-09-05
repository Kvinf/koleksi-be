#if DEBUG
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Infrastructure;

namespace Koleksi.Api.Development;

/// <summary>
/// Persists <see cref="LocalIdentityService"/>'s accounts to the local DynamoDB table
/// instead of an in-memory dictionary, so a restarted API does not forget every account
/// registered against it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow: only the account itself — subject, username, email, password,
/// confirmed flag — moves here. Refresh tokens, OTP challenges, and pending sign-up
/// confirmation codes stay in <see cref="LocalIdentityService"/>'s in-memory dictionaries,
/// because losing those on a restart is no different from what happens to a real, deployed
/// session anyway — a signed-in tab has to sign in again either way, since
/// <see cref="LocalSigningKey"/> is also fresh per process. The actual annoyance this
/// exists to fix is narrower: "I have to register from scratch again."
/// </para>
/// <para>
/// A separate item type on the same table <c>LocalIdentityService</c>'s own data never
/// otherwise touches — <c>pk = "DEVACCOUNT#{email}"</c>, <c>sk = "#DEVACCOUNT"</c>, chosen
/// so it never appears in any GSI (<c>gsi_seller</c>, <c>gsi_browse_*</c>,
/// <c>gsi_auction_end</c> all require their own <c>gsi_*_pk</c>/<c>gsi_*_sk</c> attributes,
/// which this item never writes) and cannot collide with any business item's key. No change
/// to <c>infra/lib/data-stack.ts</c> or <c>scripts/dev-table.cs</c> is needed for it — an
/// item needs a table-level or GSI-level key attribute declared in advance, and this one
/// uses only the two that already are.
/// </para>
/// <para>
/// Still local-dev-only in every sense that mattered before: DynamoDB Local itself runs
/// with <c>-inMemory</c> (see <c>docker-compose.yml</c>), so nothing here reaches an actual
/// disk either — only the boundary of what survives moved from "this process" to "this
/// process and this container", not to "forever". <c>docker compose down</c> forgets these
/// accounts exactly as the old in-memory dictionary forgot them on every restart.
/// </para>
/// </remarks>
internal sealed class LocalAccountStore(IAmazonDynamoDB dynamo, DynamoOptions options)
{
    private const string PkPrefix = "DEVACCOUNT#";
    private const string SkValue = "#DEVACCOUNT";

    public async Task<LocalAccount?> FindByEmailAsync(string email, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = options.TableName, Key = Key(email) },
            ct).ConfigureAwait(false);

        // DynamoDB Local returns a null Item (not an empty dictionary) for a miss, unlike
        // the real service — the null-check has to come first or a lookup for an email
        // with no account throws instead of answering "no account".
        return response.Item is not { Count: > 0 } item ? null : ToAccount(item);
    }

    /// <summary>
    /// Scans for a username match. Local dev only ever holds a handful of these accounts —
    /// a <c>Query</c> would need a GSI this item deliberately does not have (see the class
    /// remarks), and adding one only to serve a lookup this rare, this small, would cost
    /// more than the scan does.
    /// </summary>
    public async Task<LocalAccount?> FindByUsernameAsync(string username, CancellationToken ct)
    {
        var response = await dynamo.ScanAsync(
            new ScanRequest
            {
                TableName = options.TableName,
                FilterExpression = "sk = :sk AND username = :username",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":sk"] = new AttributeValue { S = SkValue },
                    [":username"] = new AttributeValue { S = username },
                },
            },
            ct).ConfigureAwait(false);

        return response.Items.Count == 0 ? null : ToAccount(response.Items[0]);
    }

    /// <summary>Scans for a subject match — see <see cref="FindByUsernameAsync"/> for why.</summary>
    public async Task<LocalAccount?> FindBySubjectAsync(string subject, CancellationToken ct)
    {
        var response = await dynamo.ScanAsync(
            new ScanRequest
            {
                TableName = options.TableName,
                FilterExpression = "sk = :sk AND subject = :subject",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":sk"] = new AttributeValue { S = SkValue },
                    [":subject"] = new AttributeValue { S = subject },
                },
            },
            ct).ConfigureAwait(false);

        return response.Items.Count == 0 ? null : ToAccount(response.Items[0]);
    }

    /// <summary>
    /// Creates the account, or returns <c>false</c> if the email is already taken.
    /// </summary>
    /// <remarks>
    /// The condition makes email-uniqueness atomic at the table. Username-uniqueness (the
    /// caller's separate <see cref="FindByUsernameAsync"/> check before calling this) is
    /// not — two registrations for different, still-unclaimed emails but the same username
    /// could both pass that check before either writes. Worth naming rather than hiding:
    /// acceptable for a single developer clicking through a sign-up form, not something a
    /// production identity provider could get away with.
    /// </remarks>
    public async Task<bool> TryCreateAsync(LocalAccount account, CancellationToken ct)
    {
        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = ToItem(account),
                    ConditionExpression = "attribute_not_exists(pk)",
                },
                ct).ConfigureAwait(false);

            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>Overwrites the account — used to flip <see cref="LocalAccount.Confirmed"/>.</summary>
    public Task SaveAsync(LocalAccount account, CancellationToken ct) =>
        dynamo.PutItemAsync(new PutItemRequest { TableName = options.TableName, Item = ToItem(account) }, ct);

    private static Dictionary<string, AttributeValue> Key(string email) => new(StringComparer.Ordinal)
    {
        ["pk"] = new AttributeValue { S = PkPrefix + email.Trim().ToLowerInvariant() },
        ["sk"] = new AttributeValue { S = SkValue },
    };

    private static Dictionary<string, AttributeValue> ToItem(LocalAccount account)
    {
        var item = Key(account.Email);
        item["subject"] = new AttributeValue { S = account.Subject };
        item["username"] = new AttributeValue { S = account.Username };
        item["email"] = new AttributeValue { S = account.Email };
        item["password"] = new AttributeValue { S = account.Password };
        item["confirmed"] = new AttributeValue { BOOL = account.Confirmed };
        return item;
    }

    private static LocalAccount ToAccount(Dictionary<string, AttributeValue> item) => new(
        item["subject"].S,
        item["username"].S,
        item["email"].S,
        item["password"].S,
        item["confirmed"].BOOL ?? false);
}

/// <param name="Subject">The <c>sub</c> claim, and therefore the seller id on a listing.</param>
/// <param name="Password">
/// Held in plain text, acceptable for exactly one reason: this store never leaves local
/// DynamoDB Local's own in-memory table (see the class remarks) and never touches a disk.
/// It is also why the type is unreachable outside a Debug build.
/// </param>
/// <param name="Confirmed">
/// Whether the emailed code from registration has been submitted. Starts <c>false</c> for a
/// self-signed-up account and <c>true</c> for the Google dev account, mirroring the real
/// pool's <c>autoVerify:{email:true}</c> — see <c>LocalIdentityService.SignInAsync</c> and
/// <c>VerifyEmailOtpAsync</c>, which both refuse to sign in an account that is still
/// <c>false</c> here.
/// </param>
internal sealed record LocalAccount(string Subject, string Username, string Email, string Password, bool Confirmed);
#endif
