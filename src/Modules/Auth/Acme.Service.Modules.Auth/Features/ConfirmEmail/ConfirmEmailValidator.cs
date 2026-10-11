using Acme.Service.Modules.Auth.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.ConfirmEmail;

internal sealed class ConfirmEmailValidator : AbstractValidator<ConfirmEmailCommand>
{
    public ConfirmEmailValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.Token).NotEmpty().MaximumLength(AccountValidation.TokenMaxLength);
    }
}
