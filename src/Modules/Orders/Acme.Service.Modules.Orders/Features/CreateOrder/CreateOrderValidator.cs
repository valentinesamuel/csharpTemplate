using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.CreateOrder;

internal sealed class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(c => c.CustomerId).NotEmpty();
        RuleFor(c => c.Currency)
            .Must(Order.IsValidCurrency)
            .WithMessage("Currency must be an ISO 4217 code of three upper-case letters.");
        RuleFor(c => c.Lines).Must(OrderLineRules.HasValidCount).WithMessage(OrderLineRules.LinesMessage);
        RuleForEach(c => c.Lines).NotNull().SetValidator(new OrderLineRequestValidator());
    }
}
