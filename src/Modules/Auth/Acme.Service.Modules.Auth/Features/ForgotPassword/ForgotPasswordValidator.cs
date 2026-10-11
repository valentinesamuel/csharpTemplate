using Acme.Service.Modules.Auth.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.ForgotPassword;

internal sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator()
    {
        RuleFor(c => c.Email).ValidEmail();
    }
}
