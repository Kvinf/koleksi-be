using Amazon.CognitoIdentityProvider.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure.Identity;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>How the pre-sign-up trigger's refusal becomes the code the sign-up form reacts to.</summary>
public sealed class EmailRegisteredTests
{
    [Theory]
    [InlineData("PreSignUp failed with error EMAIL_REGISTERED:google.", "google")]
    [InlineData("PreSignUp failed with error EMAIL_REGISTERED:password.", "password")]
    public void The_triggers_refusal_names_how_the_existing_account_signs_in(string message, string expected) =>
        CognitoIdentityService.EmailRegisteredKind(new UserLambdaValidationException(message)).ShouldBe(expected);

    [Theory]
    [InlineData("PreSignUp failed with error EMAIL_REGISTERED:facebook.")]
    [InlineData("PreSignUp failed with error something else.")]
    [InlineData("")]
    public void Any_other_trigger_failure_is_not_read_as_an_existing_account(string message) =>
        CognitoIdentityService.EmailRegisteredKind(new UserLambdaValidationException(message)).ShouldBeNull();

    [Fact]
    public void The_failure_code_the_website_reads_is_the_prefix_plus_how_the_account_signs_in()
    {
        RegistrationOutcome.EmailRegistered(EmailAccount.Google).FailureCode.ShouldBe("auth.email_registered_google");
        RegistrationOutcome.EmailRegistered(EmailAccount.Password).FailureCode.ShouldBe("auth.email_registered_password");
        RegistrationOutcome.EmailRegistered(EmailAccount.Google).Succeeded.ShouldBeFalse();
    }
}
