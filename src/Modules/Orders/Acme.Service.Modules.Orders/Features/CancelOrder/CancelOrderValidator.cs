using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.CancelOrder;

internal sealed class CancelOrderValidator : AbstractValidator<CancelOrderCommand>
{
    public CancelOrderValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.Caller).NotNull();
    }
}
