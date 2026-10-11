using Acme.Service.Modules.Auth.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.MfaDisable;

internal sealed class MfaDisableValidator : AbstractValidator<MfaDisableCommand>
{
    public MfaDisableValidator()
    {
        RuleFor(c => c.Password).PresentPassword();
    }
}
