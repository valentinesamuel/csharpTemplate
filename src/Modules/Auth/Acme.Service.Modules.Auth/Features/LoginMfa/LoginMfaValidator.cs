using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Mfa;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.LoginMfa;

/// <summary>Shape only: an mfa token and exactly one of a six-digit code or a recovery code. Messages never echo values.</summary>
internal sealed class LoginMfaValidator : AbstractValidator<LoginMfaCommand>
{
    public LoginMfaValidator()
    {
        RuleFor(c => c.MfaToken).NotEmpty().MaximumLength(MfaTokenIssuer.MaxTokenLength);
        RuleFor(c => c)
            .Must(c => string.IsNullOrEmpty(c.Code) != string.IsNullOrEmpty(c.RecoveryCode))
            .OverridePropertyName("code")
            .WithMessage("Send either an authenticator code or a recovery code.");
        When(c => !string.IsNullOrEmpty(c.Code), () => RuleFor(c => c.Code).AuthenticatorCode());
        When(c => !string.IsNullOrEmpty(c.RecoveryCode), () => RuleFor(c => c.RecoveryCode).MaximumLength(AccountValidation.RecoveryCodeMaxLength));
    }
}
