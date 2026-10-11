using Acme.Service.Modules.Auth.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.ResetPassword;

/// <summary>Presence and maximum lengths; the length policy of the new password is Identity's (one source of truth).</summary>
internal sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.Token).NotEmpty().MaximumLength(AccountValidation.TokenMaxLength);
        RuleFor(c => c.NewPassword).PresentPassword();
    }
}
