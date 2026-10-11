using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.ListOrders;

internal sealed class ListOrdersValidator : AbstractValidator<ListOrdersQuery>
{
    public ListOrdersValidator()
    {
        RuleFor(q => q.Caller).NotNull();
        RuleFor(q => q.Page).NotNull();
        RuleFor(q => q.Sort).NotNull();
        RuleFor(q => q.CreatedTo)
            .Must((query, createdTo) => query.CreatedFrom is null || createdTo is null || query.CreatedFrom <= createdTo)
            .WithName("createdTo")
            .WithMessage("createdTo must not be before createdFrom.");
    }
}
