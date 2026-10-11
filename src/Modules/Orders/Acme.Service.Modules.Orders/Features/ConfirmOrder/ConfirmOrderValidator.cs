using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.ConfirmOrder;

internal sealed class ConfirmOrderValidator : AbstractValidator<ConfirmOrderCommand>
{
    public ConfirmOrderValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
    }
}
