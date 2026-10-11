using System.Text;
using Acme.Service.Modules.Auth.Features.ChangePassword;
using Acme.Service.Modules.Auth.Features.Login;
using Acme.Service.Modules.Auth.Features.LoginMfa;
using Acme.Service.Modules.Auth.Features.MfaSetup;
using Acme.Service.Modules.Auth.Features.ResetPassword;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Mfa;
using Acme.Service.Modules.Auth.Sessions;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Unit;

/// <summary>The TOTP arithmetic (RFC 6238 vectors), the LoginMfa validator, the otpauth URI and secret-free ToString (INV-28).</summary>
public sealed class MfaUnitTests
{
    // RFC 6238 Appendix B, SHA-1 seed; the 8-digit reference values truncated to the 6 digits authenticator apps use.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void ComputeCode_Rfc6238Vectors_MatchReference(long unixSeconds, string expected)
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        TotpReplayGuard.ComputeCode(key, TotpReplayGuard.TimeStepOf(time)).ShouldBe(expected);
    }

    [Fact]
    public void ComputeCode_Base32Key_MatchesTheTestAuthenticator()
    {
        const string key = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
        var time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_015);

        TotpReplayGuard.TryDecodeBase32(key, out var bytes).ShouldBeTrue();
        TotpReplayGuard.ComputeCode(bytes, TotpReplayGuard.TimeStepOf(time)).ShouldBe(Rfc6238.Code(key, time));
        TotpReplayGuard.TryDecodeBase32("not base32!", out _).ShouldBeFalse();
        TotpReplayGuard.TryDecodeBase32(string.Empty, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("token", "123456", null, true)]
    [InlineData("token", null, "ABCDE-12345", true)]
    [InlineData("token", null, null, false)]
    [InlineData("token", "123456", "ABCDE-12345", false)]
    [InlineData("token", "12345", null, false)]
    [InlineData("token", "12a456", null, false)]
    [InlineData("", "123456", null, false)]
    public void LoginMfaValidator_Input_RequiresTokenAndExactlyOneCode(string token, string? code, string? recoveryCode, bool valid)
    {
        var result = new LoginMfaValidator().Validate(new LoginMfaCommand(token, code, recoveryCode, ClientInfo.None));

        result.IsValid.ShouldBe(valid);
        result.Errors.ShouldAllBe(e => code == null || !e.ErrorMessage.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    public void AuthenticatorUri_EncodesIssuerAndAccount()
    {
        MfaSetupUseCase.AuthenticatorUri("Example Service", "a+b@example.com", "JBSWY3DPEHPK3PXP")
            .ShouldBe("otpauth://totp/Example%20Service:a%2Bb%40example.com?secret=JBSWY3DPEHPK3PXP&issuer=Example%20Service&digits=6&period=30");
    }

    [Fact]
    public void Inputs_ToString_NeverContainSecrets()
    {
        object[] values =
        [
            new LoginMfaRequest("mfa-token-value", "123456", "RECOV-ERY12"),
            new LoginMfaCommand("mfa-token-value", "123456", "RECOV-ERY12", ClientInfo.None),
            new ChangePasswordRequest("old-secret-value", "new-secret-value"),
            new ChangePasswordCommand(Guid.Empty, Guid.Empty, "old-secret-value", "new-secret-value"),
            new ResetPasswordCommand(Guid.Empty, "reset-token-value", "new-secret-value"),
            new MfaSetupResponse("SHAREDKEYVALUE", "otpauth://totp/x:y?secret=SHAREDKEYVALUE"),
            new RecoveryCodesResponse(["RECOV-ERY12"]),
            new MfaRequiredResponse(true, "mfa-token-value"),
            new MfaToken("mfa-token-value", DateTimeOffset.UnixEpoch),
            new PasswordConfirmationRequest("old-secret-value"),
        ];

        foreach (var value in values)
        {
            var text = value.ToString()!;
            foreach (var secret in new[] { "mfa-token-value", "123456", "RECOV-ERY12", "old-secret-value", "new-secret-value", "reset-token-value", "SHAREDKEYVALUE" })
            {
                text.ShouldNotContain(secret);
            }
        }
    }
}
