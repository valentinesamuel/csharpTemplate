using Acme.Service.Modules.Auth.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.ChangePassword;

/// <summary>Presence and maximum length; the length policy of the new password is Identity's (one source of truth).</summary>
internal sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(c => c.CurrentPassword).PresentPassword();
        RuleFor(c => c.NewPassword).PresentPassword();
    }
}
