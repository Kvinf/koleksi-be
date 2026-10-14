namespace Koleksi.Application.Abstractions;

/// <summary>
/// Limits how often one account can ask for a verification code, and how many guesses a code
/// survives.
/// </summary>
/// <remarks>
/// <para>
/// Every request sends a real SMS or email that costs money and lands on somebody else's phone,
/// so the limits are enforced server-side and per account: a cooldown between requests to the
/// same destination, and a cap per hour across all destinations. They sit behind the per-user
/// rate-limit policy, which is only a coarse bound in front of these.
/// </para>
/// </remarks>
public interface IVerificationThrottle
{
    /// <summary>Counts a code request, or says why it must wait.</summary>
    /// <param name="userId">The account asking.</param>
    /// <param name="destination">The phone number or email address the code would go to.</param>
    Task<VerificationThrottleResult> TryAcquireAsync(string userId, string destination, CancellationToken ct);
}

public enum VerificationThrottleResult
{
    Allowed = 0,

    /// <summary>A code to this destination was requested less than <see cref="VerificationLimits.Cooldown"/> ago.</summary>
    Cooldown = 1,

    /// <summary>The account has already requested <see cref="VerificationLimits.MaxCodesPerHour"/> codes this hour.</summary>
    HourlyLimit = 2,
}

/// <summary>The numbers behind verification codes, in one place so the adapters and the endpoint agree.</summary>
public static class VerificationLimits
{
    /// <summary>Minimum gap between two codes to the same destination for the same account.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    /// <summary>Most codes one account may request per clock hour, to any destination.</summary>
    public const int MaxCodesPerHour = 5;

    /// <summary>How many wrong guesses a pending code survives before it is invalidated.</summary>
    public const int MaxAttempts = 5;
}
