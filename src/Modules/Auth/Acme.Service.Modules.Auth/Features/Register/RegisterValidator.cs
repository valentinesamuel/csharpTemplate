using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Options;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Features.Register;

/// <summary>
/// Shape and password policy of a registration. It runs before the account lookup, so an invalid request gets the same
/// 400 whether or not the email is taken (D-23).
/// </summary>
internal sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator(IOptions<AuthIdentityOptions> identityOptions)
    {
        ArgumentNullException.ThrowIfNull(identityOptions);

        RuleFor(c => c.Email).ValidEmail();
        RuleFor(c => c.Password)
            .NotEmpty()
            .MinimumLength(identityOptions.Value.Password.RequiredLength)
            .MaximumLength(AuthIdentityOptions.MaximumPasswordLength);
        RuleFor(c => c.DisplayName).MaximumLength(AuthUser.DisplayNameMaxLength);
    }
}
