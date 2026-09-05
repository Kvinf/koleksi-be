using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IPhoneVerificationService"/>.</summary>
public sealed class FakePhoneVerificationService : FakeAdapter, IPhoneVerificationService
{
    private readonly Dictionary<string, string> _codes = new(StringComparer.Ordinal);

    /// <summary>The code sent to each number, so a test can verify with it instead of guessing.</summary>
    public IReadOnlyDictionary<string, string> SentCodes => _codes;

    public Task<string?> RequestCodeAsync(string phone, CancellationToken ct)
    {
        ThrowIfFailing();
        _codes[phone] = "654321";
        return Task.FromResult<string?>("654321");
    }

    public Task<bool> VerifyCodeAsync(string phone, string code, CancellationToken ct)
    {
        ThrowIfFailing();

        // One-shot, matching both real implementations: removed on every attempt.
        if (_codes.Remove(phone, out var stored) && string.Equals(stored, code, StringComparison.Ordinal))
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }
}
