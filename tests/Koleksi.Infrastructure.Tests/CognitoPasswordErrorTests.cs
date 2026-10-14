using Amazon.CognitoIdentityProvider.Model;
using Koleksi.Infrastructure.Identity;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// How Cognito's refusals become the stable codes the frontend translates.
/// </summary>
/// <remarks>
/// The mapping is a pure function precisely so this can be tested without a pool: the property
/// that matters is that a reset never separates "no such account" from "wrong code".
/// </remarks>
public sealed class CognitoPasswordErrorTests
{
    private const CognitoIdentityService.PasswordOperation Reset = CognitoIdentityService.PasswordOperation.Reset;
    private const CognitoIdentityService.PasswordOperation Change = CognitoIdentityService.PasswordOperation.Change;

    [Fact]
    public void A_wrong_code_an_unknown_account_and_an_unconfirmed_account_are_one_answer_for_a_reset()
    {
        CognitoIdentityService.PasswordFailureCode(new CodeMismatchException("x"), Reset).ShouldBe("auth.code_invalid");
        CognitoIdentityService.PasswordFailureCode(new UserNotFoundException("x"), Reset).ShouldBe("auth.code_invalid");
        CognitoIdentityService.PasswordFailureCode(new UserNotConfirmedException("x"), Reset).ShouldBe("auth.code_invalid");
        CognitoIdentityService.PasswordFailureCode(new NotAuthorizedException("x"), Reset).ShouldBe("auth.code_invalid");

        // A state refusal must not read differently from a wrong code either.
        CognitoIdentityService.PasswordFailureCode(new InvalidParameterException("x"), Reset).ShouldBe("auth.code_invalid");
    }

    [Fact]
    public void An_expired_code_a_weak_password_and_throttling_have_their_own_codes()
    {
        foreach (var operation in new[] { Reset, Change })
        {
            CognitoIdentityService.PasswordFailureCode(new ExpiredCodeException("x"), operation).ShouldBe("auth.code_expired");
            CognitoIdentityService.PasswordFailureCode(new InvalidPasswordException("x"), operation).ShouldBe("auth.password_too_weak");
            CognitoIdentityService.PasswordFailureCode(new LimitExceededException("x"), operation).ShouldBe("auth.try_again_later");
            CognitoIdentityService.PasswordFailureCode(new TooManyRequestsException("x"), operation).ShouldBe("auth.try_again_later");
        }
    }

    [Fact]
    public void For_a_change_a_refusal_of_the_token_is_reported_as_the_wrong_current_password()
    {
        CognitoIdentityService.PasswordFailureCode(new NotAuthorizedException("x"), Change)
            .ShouldBe("auth.current_password_invalid");
        CognitoIdentityService.PasswordFailureCode(new InvalidParameterException("x"), Change)
            .ShouldBe("auth.invalid_details");
    }

    [Fact]
    public void An_exception_this_flow_has_no_answer_for_is_not_swallowed()
    {
        // Mapped to null, so the catch filter does not match and it propagates as an error.
        CognitoIdentityService.PasswordFailureCode(new ResourceNotFoundException("x"), Reset).ShouldBeNull();
        CognitoIdentityService.PasswordFailureCode(new UserNotFoundException("x"), Change).ShouldBeNull();
    }

    [Fact]
    public void A_google_user_is_recognised_by_its_username_or_its_identities_attribute()
    {
        CognitoIdentityService.IsFederated("Google_1098", []).ShouldBeTrue();
        CognitoIdentityService.IsFederated(
            "budi",
            [new AttributeType { Name = "identities", Value = "[{\"providerName\":\"Google\"}]" }]).ShouldBeTrue();

        CognitoIdentityService.IsFederated(
            "budi",
            [new AttributeType { Name = "email", Value = "budi@example.test" }]).ShouldBeFalse();
        CognitoIdentityService.IsFederated("budi", null).ShouldBeFalse();
    }
}
