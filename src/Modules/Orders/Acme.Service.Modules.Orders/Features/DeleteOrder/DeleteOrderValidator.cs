using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.DeleteOrder;

internal sealed class DeleteOrderValidator : AbstractValidator<DeleteOrderCommand>
{
    public DeleteOrderValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
    }
}
