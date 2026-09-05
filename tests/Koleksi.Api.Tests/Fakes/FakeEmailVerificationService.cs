using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IEmailVerificationService"/>.</summary>
public sealed class FakeEmailVerificationService : FakeAdapter, IEmailVerificationService
{
    private readonly Dictionary<string, string> _codes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The code sent to each address, so a test can verify with it instead of guessing.</summary>
    public IReadOnlyDictionary<string, string> SentCodes => _codes;

    public Task<string?> RequestCodeAsync(string email, CancellationToken ct)
    {
        ThrowIfFailing();
        _codes[email] = "135790";
        return Task.FromResult<string?>("135790");
    }

    public Task<bool> VerifyCodeAsync(string email, string code, CancellationToken ct)
    {
        ThrowIfFailing();

        // One-shot, matching both real implementations: removed on every attempt.
        if (_codes.Remove(email, out var stored) && string.Equals(stored, code, StringComparison.Ordinal))
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }
}
