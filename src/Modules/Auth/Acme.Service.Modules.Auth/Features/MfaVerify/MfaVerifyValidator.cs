using Acme.Service.Modules.Auth.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.MfaVerify;

internal sealed class MfaVerifyValidator : AbstractValidator<MfaVerifyCommand>
{
    public MfaVerifyValidator()
    {
        RuleFor(c => c.Code).AuthenticatorCode();
    }
}
