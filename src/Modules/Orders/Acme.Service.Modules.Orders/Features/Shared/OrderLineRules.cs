using Acme.Service.Modules.Orders.Domain;
using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.Shared;

/// <summary>Messages of the line-count rule shared by the commands that carry order lines (create, replace lines).</summary>
internal static class OrderLineRules
{
    public static readonly string LinesMessage = $"An order has 1 to {Order.MaxLines} lines.";

    /// <summary>True for 1 to <see cref="Order.MaxLines"/> lines.</summary>
    public static bool HasValidCount(IReadOnlyList<OrderLineRequest>? lines) => lines is { Count: > 0 and <= Order.MaxLines };
}

/// <summary>The domain limits of one line (<see cref="OrderLine"/>).</summary>
internal sealed class OrderLineRequestValidator : AbstractValidator<OrderLineRequest>
{
    public OrderLineRequestValidator()
    {
        RuleFor(l => l.Sku)
            .Must(OrderLine.IsValidSku)
            .WithMessage($"A SKU is 1 to {OrderLine.SkuMaxLength} characters without surrounding white space.");
        RuleFor(l => l.Quantity)
            .Must(q => q is { } quantity && OrderLine.IsValidQuantity(quantity))
            .WithMessage($"Quantity must be between 1 and {OrderLine.MaxQuantity}.");
        RuleFor(l => l.UnitPrice)
            .Must(p => p is { } price && OrderLine.IsValidUnitPrice(price))
            .WithMessage($"Unit price must be between 0 and {OrderLine.MaxUnitPrice} with at most {OrderLine.PriceScale} decimals.");
    }
}
