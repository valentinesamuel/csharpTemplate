using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.GetOrder;

internal sealed class GetOrderValidator : AbstractValidator<GetOrderQuery>
{
    public GetOrderValidator()
    {
        RuleFor(q => q.OrderId).NotEmpty();
        RuleFor(q => q.Caller).NotNull();
    }
}
