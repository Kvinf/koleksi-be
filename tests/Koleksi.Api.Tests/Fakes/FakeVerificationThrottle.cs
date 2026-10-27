using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IVerificationThrottle"/> with the DynamoDB adapter's rules: a cooldown
/// per (account, destination), and a cap per account per clock hour.
/// </summary>
public sealed class FakeVerificationThrottle(IClock clock) : FakeAdapter, IVerificationThrottle
{
    private readonly object _gate = new();
    private readonly Dictionary<(string User, string Destination), DateTimeOffset> _cooldownEnds = [];
    private readonly Dictionary<(string User, string Hour), int> _perHour = [];

    public Task<VerificationThrottleResult> TryAcquireAsync(string userId, string destination, CancellationToken ct)
    {
        ThrowIfFailing();

        lock (_gate)
        {
            var now = clock.UtcNow;
            var cooldownKey = (userId, destination.Trim().ToLowerInvariant());

            if (_cooldownEnds.TryGetValue(cooldownKey, out var until) && until > now)
            {
                return Task.FromResult(VerificationThrottleResult.Cooldown);
            }

            _cooldownEnds[cooldownKey] = now + VerificationLimits.Cooldown;

            var hourKey = (userId, now.UtcDateTime.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture));
            var used = _perHour.GetValueOrDefault(hourKey);
            if (used >= VerificationLimits.MaxCodesPerHour)
            {
                return Task.FromResult(VerificationThrottleResult.HourlyLimit);
            }

            _perHour[hourKey] = used + 1;
            return Task.FromResult(VerificationThrottleResult.Allowed);
        }
    }
}
