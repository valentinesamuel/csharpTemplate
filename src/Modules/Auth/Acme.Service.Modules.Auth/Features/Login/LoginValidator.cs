using Acme.Service.Modules.Auth.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.Login;

/// <summary>Presence and length only; whether the account exists is never part of validation (D-23).</summary>
internal sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(c => c.Email).NotEmpty().MaximumLength(AccountValidation.EmailMaxLength);
        RuleFor(c => c.Password).PresentPassword();
    }
}
