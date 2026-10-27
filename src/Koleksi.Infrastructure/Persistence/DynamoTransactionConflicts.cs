using Amazon.DynamoDBv2.Model;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Decides whether a cancelled DynamoDB transaction lost an optimistic-concurrency race.
/// </summary>
/// <remarks>
/// <para>
/// A transaction is cancelled for two different reasons that both mean "somebody else got
/// there first". <c>ConditionalCheckFailed</c> says a condition we wrote (a version check, an
/// "item must not exist" guard) no longer holds. <c>TransactionConflict</c> says another
/// transaction is touching one of the same items at the same instant, so DynamoDB refused to
/// run both; the loser is cancelled with that reason before any condition is even evaluated.
/// Both are expected under contention and both are retryable by re-reading, so every
/// repository that turns a lost race into <see cref="Koleksi.Application.Abstractions.ConcurrencyConflictException"/>
/// asks this one type rather than matching a reason code of its own. Matching only the first
/// is what let two simultaneous bids surface as a 500.
/// </para>
/// <para>
/// Throttling and capacity reasons are deliberately not here: they say the table is busy, not
/// that the data changed, and re-reading would not change the outcome.
/// </para>
/// </remarks>
internal static class DynamoTransactionConflicts
{
    internal const string ConditionalCheckFailed = "ConditionalCheckFailed";

    internal const string TransactionConflict = "TransactionConflict";

    /// <summary>True when any reason is a failed condition or a conflicting transaction.</summary>
    public static bool IsConflict(TransactionCanceledException cancelled)
    {
        ArgumentNullException.ThrowIfNull(cancelled);

        return cancelled.CancellationReasons?.Exists(reason => IsConflictCode(reason.Code)) == true;
    }

    /// <summary>True when any reason is a conflicting transaction rather than a failed condition.</summary>
    public static bool IsTransactionConflict(TransactionCanceledException cancelled)
    {
        ArgumentNullException.ThrowIfNull(cancelled);

        return cancelled.CancellationReasons?.Exists(
            reason => string.Equals(reason.Code, TransactionConflict, StringComparison.Ordinal)) == true;
    }

    private static bool IsConflictCode(string? code) =>
        string.Equals(code, ConditionalCheckFailed, StringComparison.Ordinal)
        || string.Equals(code, TransactionConflict, StringComparison.Ordinal);
}
