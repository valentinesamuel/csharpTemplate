using Acme.Service.Modules.Auth.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.MfaRecoveryCodes;

internal sealed class MfaRecoveryCodesValidator : AbstractValidator<MfaRecoveryCodesCommand>
{
    public MfaRecoveryCodesValidator()
    {
        RuleFor(c => c.Password).PresentPassword();
    }
}
