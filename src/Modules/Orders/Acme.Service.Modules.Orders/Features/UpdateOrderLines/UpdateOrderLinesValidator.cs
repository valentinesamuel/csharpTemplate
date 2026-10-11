using Acme.Service.Modules.Orders.Features.Shared;
using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.UpdateOrderLines;

internal sealed class UpdateOrderLinesValidator : AbstractValidator<UpdateOrderLinesCommand>
{
    public UpdateOrderLinesValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.Caller).NotNull();
        RuleFor(c => c.Version).NotNull().WithMessage("The version of the order as last read is required.");
        RuleFor(c => c.Lines).Must(OrderLineRules.HasValidCount).WithMessage(OrderLineRules.LinesMessage);
        RuleForEach(c => c.Lines).NotNull().SetValidator(new OrderLineRequestValidator());
    }
}
