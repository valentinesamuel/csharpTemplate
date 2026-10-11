using Acme.Service.Modules.Auth.Features.Login;
using Acme.Service.Modules.Auth.Features.Register;
using Acme.Service.Modules.Auth.Notifications;
using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Unit;

/// <summary>Validators, email templates and the secret-free <c>ToString</c> of the account inputs.</summary>
public sealed class AccountValidationTests
{
    private const string Password = "correct horse battery";

    [Theory]
    [InlineData("a@example.com", Password, null, true)]
    [InlineData("not-an-email", Password, null, false)]
    [InlineData("", Password, null, false)]
    [InlineData("a@example.com", "eleven char", null, false)]
    [InlineData("a@example.com", Password, "a display name that is just fine", true)]
    public void RegisterValidator_Input_ValidatesShapeAndPasswordLength(string email, string password, string? displayName, bool valid)
    {
        var validator = new RegisterValidator(Microsoft.Extensions.Options.Options.Create(new AuthIdentityOptions()));

        validator.Validate(new RegisterCommand(email, password, displayName)).IsValid.ShouldBe(valid);
    }

    [Fact]
    public void RegisterValidator_LongDisplayNameOrPassword_Fails()
    {
        var validator = new RegisterValidator(Microsoft.Extensions.Options.Options.Create(new AuthIdentityOptions()));

        validator.Validate(new RegisterCommand("a@example.com", Password, new string('x', 101))).IsValid.ShouldBeFalse();
        validator.Validate(new RegisterCommand("a@example.com", new string('x', 129), null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void LoginValidator_MissingPassword_FailsWithoutEchoingInput()
    {
        var result = new LoginValidator().Validate(new LoginCommand("a@example.com", "", ClientInfo.None));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(LoginCommand.Password));
    }

    [Fact]
    public void Inputs_ToString_NeverContainSecrets()
    {
        new RegisterCommand("a@example.com", Password, null).ToString().ShouldNotContain(Password);
        new LoginCommand("a@example.com", Password, ClientInfo.None).ToString().ShouldNotContain(Password);
        new LoginRequest("a@example.com", Password).ToString().ShouldNotContain(Password);
    }

    [Fact]
    public void BuildLink_TokenWithReservedCharacters_IsEncodedAndRoundTrips()
    {
        var userId = Guid.CreateVersion7();
        const string token = "CfDJ8+abc/def==&x=1";

        var link = EmailTemplates.BuildLink("https://app.example.com/confirm?lang=en", userId, token);

        var query = QueryHelpers.ParseQuery(new Uri(link).Query);
        query["lang"].ToString().ShouldBe("en");
        query["userId"].ToString().ShouldBe(userId.ToString());
        query["token"].ToString().ShouldBe(token);
        link.ShouldNotContain("+abc");
    }

    [Fact]
    public void EmailVerification_Link_InTextAndHtmlEncodedInHtml()
    {
        const string link = "https://app.example.com/confirm?userId=1&token=a\"b<c";

        var message = EmailTemplates.EmailVerification("a@example.com", link, TimeSpan.FromHours(1));

        message.To.ShouldBe("a@example.com");
        message.Subject.ShouldBe(EmailTemplates.EmailVerificationSubject);
        message.TextBody.ShouldContain(link);
        message.TextBody.ShouldContain("expires in 1 hour");
        message.HtmlBody.ShouldNotBeNull().ShouldNotContain("a\"b<c");
        message.HtmlBody.ShouldContain("&amp;token=");
    }

    [Theory]
    [InlineData(60, "1 hour")]
    [InlineData(180, "3 hours")]
    [InlineData(30, "30 minutes")]
    [InlineData(90, "90 minutes")]
    [InlineData(1, "1 minute")]
    public void Describe_Lifespan_IsHumanReadable(int minutes, string expected)
    {
        EmailTemplates.Describe(TimeSpan.FromMinutes(minutes)).ShouldBe(expected);
    }
}
