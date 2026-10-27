using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IEmailVerificationService"/>, with the same rules as <see cref="FakePhoneVerificationService"/>.</summary>
public sealed class FakeEmailVerificationService : FakeAdapter, IEmailVerificationService
{
    private readonly Dictionary<string, string> _sent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Owner, string Email), (string Code, int Attempts)> _pending = [];

    /// <summary>The code last sent to each address, so a test can verify with it instead of guessing.</summary>
    public IReadOnlyDictionary<string, string> SentCodes => _sent;

    public int RequestCount { get; private set; }

    public Task<string?> RequestCodeAsync(string ownerId, string email, CancellationToken ct)
    {
        ThrowIfFailing();
        RequestCount++;
        _sent[email] = "135790";
        _pending[(ownerId, email.ToLowerInvariant())] = ("135790", 0);
        return Task.FromResult<string?>("135790");
    }

    public Task<bool> VerifyCodeAsync(string ownerId, string email, string code, CancellationToken ct)
    {
        ThrowIfFailing();

        var key = (ownerId, email.ToLowerInvariant());
        if (!_pending.TryGetValue(key, out var challenge))
        {
            return Task.FromResult(false);
        }

        var attempts = challenge.Attempts + 1;
        var matches = string.Equals(challenge.Code, code, StringComparison.Ordinal);

        if (matches || attempts >= VerificationLimits.MaxAttempts)
        {
            _pending.Remove(key);
        }
        else
        {
            _pending[key] = (challenge.Code, attempts);
        }

        return Task.FromResult(matches);
    }
}
