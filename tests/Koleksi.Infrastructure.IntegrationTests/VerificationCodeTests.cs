using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// The verification-code challenge and the throttle in front of it, against a real table.
/// </summary>
/// <remarks>
/// The rules that matter are all conditional writes: a wrong guess is counted rather than
/// deleting the code, concurrent guesses cannot all slip in under the cap, and two simultaneous
/// code requests cannot both pass the cooldown. A fake can restate those rules but not prove them.
/// </remarks>
public sealed class VerificationCodeTests : DynamoTestBase
{
    private const string Victim = "b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33";
    private const string Stranger = "9c2e5a71-08bd-4f36-b7aa-1e4c9d05f882";
    private const string Phone = "+6281234567890";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private Task StoreAsync(string owner, string code) =>
        Table.OtpChallenges.StoreAsync(
            DynamoTable.PhoneOtpPkValue(owner, Phone), DynamoTable.PhoneOtpSk(), "PhoneOtp", code, Lifetime, Ct);

    private Task<bool> VerifyAsync(string owner, string code) =>
        Table.OtpChallenges.VerifyAsync(DynamoTable.PhoneOtpPkValue(owner, Phone), DynamoTable.PhoneOtpSk(), code, Ct);

    // ─── The challenge ───────────────────────────────────────────────────────────

    [DynamoLocalFact]
    public async Task The_right_code_verifies_once_and_is_then_spent()
    {
        await StoreAsync(Victim, "123456");

        (await VerifyAsync(Victim, "123456")).ShouldBeTrue();
        (await VerifyAsync(Victim, "123456")).ShouldBeFalse();
    }

    [DynamoLocalFact]
    public async Task A_wrong_code_does_not_delete_the_pending_one()
    {
        await StoreAsync(Victim, "123456");

        (await VerifyAsync(Victim, "000000")).ShouldBeFalse();
        (await VerifyAsync(Victim, "123456")).ShouldBeTrue();
    }

    [DynamoLocalFact]
    public async Task A_strangers_guesses_never_touch_the_victims_challenge()
    {
        await StoreAsync(Victim, "123456");

        for (var guess = 0; guess < VerificationLimits.MaxAttempts * 2; guess++)
        {
            (await VerifyAsync(Stranger, "123456")).ShouldBeFalse();
        }

        (await VerifyAsync(Victim, "123456")).ShouldBeTrue();
    }

    [DynamoLocalFact]
    public async Task The_challenge_is_invalidated_after_the_allowed_number_of_wrong_guesses()
    {
        await StoreAsync(Victim, "123456");

        for (var guess = 0; guess < VerificationLimits.MaxAttempts; guess++)
        {
            (await VerifyAsync(Victim, "000000")).ShouldBeFalse();
        }

        // Even the right answer is too late now.
        (await VerifyAsync(Victim, "123456")).ShouldBeFalse();
    }

    [DynamoLocalFact]
    public async Task The_last_allowed_guess_can_still_be_the_right_one()
    {
        await StoreAsync(Victim, "123456");

        for (var guess = 0; guess < VerificationLimits.MaxAttempts - 1; guess++)
        {
            (await VerifyAsync(Victim, "000000")).ShouldBeFalse();
        }

        (await VerifyAsync(Victim, "123456")).ShouldBeTrue();
    }

    [DynamoLocalFact]
    public async Task Concurrent_guesses_are_each_counted_so_they_cannot_beat_the_cap()
    {
        await StoreAsync(Victim, "123456");

        // Twenty wrong guesses at once: only MaxAttempts of them may be counted, then it is gone.
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => VerifyAsync(Victim, "000000")));

        (await VerifyAsync(Victim, "123456")).ShouldBeFalse();
    }

    [DynamoLocalFact]
    public async Task An_expired_challenge_does_not_verify_even_before_the_ttl_sweep_removes_it()
    {
        await StoreAsync(Victim, "123456");

        Table.Clock.UtcNow += Lifetime + TimeSpan.FromSeconds(1);

        (await VerifyAsync(Victim, "123456")).ShouldBeFalse();
    }

    [DynamoLocalFact]
    public async Task Asking_for_a_new_code_replaces_the_old_one_and_resets_the_count()
    {
        await StoreAsync(Victim, "111111");
        for (var guess = 0; guess < VerificationLimits.MaxAttempts - 1; guess++)
        {
            await VerifyAsync(Victim, "000000");
        }

        await StoreAsync(Victim, "222222");

        (await VerifyAsync(Victim, "111111")).ShouldBeFalse();
        (await VerifyAsync(Victim, "222222")).ShouldBeTrue();
    }

    // ─── The throttle ────────────────────────────────────────────────────────────

    [DynamoLocalFact]
    public async Task A_second_request_to_the_same_destination_inside_the_cooldown_is_refused()
    {
        (await Table.VerificationThrottle.TryAcquireAsync(Victim, Phone, Ct)).ShouldBe(VerificationThrottleResult.Allowed);
        (await Table.VerificationThrottle.TryAcquireAsync(Victim, Phone, Ct)).ShouldBe(VerificationThrottleResult.Cooldown);

        Table.Clock.UtcNow += VerificationLimits.Cooldown + TimeSpan.FromSeconds(1);

        (await Table.VerificationThrottle.TryAcquireAsync(Victim, Phone, Ct)).ShouldBe(VerificationThrottleResult.Allowed);
    }

    [DynamoLocalFact]
    public async Task The_cooldown_is_per_account_and_per_destination()
    {
        (await Table.VerificationThrottle.TryAcquireAsync(Victim, Phone, Ct)).ShouldBe(VerificationThrottleResult.Allowed);

        (await Table.VerificationThrottle.TryAcquireAsync(Victim, "+6281299999999", Ct))
            .ShouldBe(VerificationThrottleResult.Allowed);
        (await Table.VerificationThrottle.TryAcquireAsync(Stranger, Phone, Ct))
            .ShouldBe(VerificationThrottleResult.Allowed);
    }

    [DynamoLocalFact]
    public async Task Concurrent_requests_to_one_destination_produce_exactly_one_winner()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Table.VerificationThrottle.TryAcquireAsync(Victim, Phone, Ct)));

        results.Count(result => result == VerificationThrottleResult.Allowed).ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task An_account_is_capped_at_five_codes_an_hour_across_destinations()
    {
        for (var i = 0; i < VerificationLimits.MaxCodesPerHour; i++)
        {
            (await Table.VerificationThrottle.TryAcquireAsync(Victim, $"+62812000000{i}", Ct))
                .ShouldBe(VerificationThrottleResult.Allowed);
        }

        (await Table.VerificationThrottle.TryAcquireAsync(Victim, "+628129999999", Ct))
            .ShouldBe(VerificationThrottleResult.HourlyLimit);

        // Another account is untouched, and the next clock hour starts a fresh count.
        (await Table.VerificationThrottle.TryAcquireAsync(Stranger, "+628129999999", Ct))
            .ShouldBe(VerificationThrottleResult.Allowed);

        Table.Clock.UtcNow = new DateTimeOffset(
            Table.Clock.UtcNow.Year, Table.Clock.UtcNow.Month, Table.Clock.UtcNow.Day, Table.Clock.UtcNow.Hour, 0, 0, TimeSpan.Zero)
            .AddHours(1).AddMinutes(1);

        (await Table.VerificationThrottle.TryAcquireAsync(Victim, "+628129999999", Ct))
            .ShouldBe(VerificationThrottleResult.Allowed);
    }

    [DynamoLocalFact]
    public async Task Concurrent_requests_cannot_exceed_the_hourly_cap()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Table.VerificationThrottle.TryAcquireAsync(Victim, $"+62813000000{i}", Ct)));

        results.Count(result => result == VerificationThrottleResult.Allowed)
            .ShouldBe(VerificationLimits.MaxCodesPerHour);
    }
}
