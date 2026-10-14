using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IPhoneVerificationService"/>.</summary>
/// <remarks>
/// Same rules as the DynamoDB-backed adapter: a challenge belongs to the account that asked, a
/// right code is spent, and a wrong one is only counted (the challenge dies after
/// <see cref="VerificationLimits.MaxAttempts"/> wrong guesses, not before).
/// </remarks>
public sealed class FakePhoneVerificationService : FakeAdapter, IPhoneVerificationService
{
    private readonly Dictionary<string, string> _sent = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Phone), (string Code, int Attempts)> _pending = [];

    /// <summary>The code last sent to each number, so a test can verify with it instead of guessing.</summary>
    public IReadOnlyDictionary<string, string> SentCodes => _sent;

    /// <summary>How many requests have reached the adapter (and so would each have sent an SMS).</summary>
    public int RequestCount { get; private set; }

    /// <summary>Whether the account still has a live challenge for the number.</summary>
    public bool HasPending(string ownerId, string phone) => _pending.ContainsKey((ownerId, phone));

    public Task<string?> RequestCodeAsync(string ownerId, string phone, CancellationToken ct)
    {
        ThrowIfFailing();
        RequestCount++;
        _sent[phone] = "654321";
        _pending[(ownerId, phone)] = ("654321", 0);
        return Task.FromResult<string?>("654321");
    }

    public Task<bool> VerifyCodeAsync(string ownerId, string phone, string code, CancellationToken ct)
    {
        ThrowIfFailing();

        if (!_pending.TryGetValue((ownerId, phone), out var challenge))
        {
            return Task.FromResult(false);
        }

        var attempts = challenge.Attempts + 1;
        var matches = string.Equals(challenge.Code, code, StringComparison.Ordinal);

        if (matches || attempts >= VerificationLimits.MaxAttempts)
        {
            _pending.Remove((ownerId, phone));
        }
        else
        {
            _pending[(ownerId, phone)] = (challenge.Code, attempts);
        }

        return Task.FromResult(matches);
    }
}
