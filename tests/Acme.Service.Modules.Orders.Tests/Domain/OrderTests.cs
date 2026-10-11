using Acme.Service.Modules.Orders.Domain;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Domain;

public sealed class OrderTests
{
    private static readonly DateTimeOffset _placedAt = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _customer = Guid.CreateVersion7();

    [Fact]
    public void Place_ValidLines_IsPendingWithTotalOfLines()
    {
        var order = Order.Place(_customer, "EUR", [new("SKU-1", 2, 10.25m), new("SKU-2", 3, 0.10m)], _placedAt);

        order.Id.ShouldNotBe(Guid.Empty);
        order.Id.Version.ShouldBe(7);
        order.CustomerId.ShouldBe(_customer);
        order.Status.ShouldBe(OrderStatus.Pending);
        order.Currency.ShouldBe("EUR");
        order.Total.ShouldBe(20.80m);
        order.CreatedAt.ShouldBe(_placedAt);
        order.UpdatedAt.ShouldBe(_placedAt);
        order.DeletedAt.ShouldBeNull();
        order.Lines.Select(l => (l.LineNumber, l.Sku, l.LineTotal)).ShouldBe([(1, "SKU-1", 20.50m), (2, "SKU-2", 0.30m)]);
    }

    [Fact]
    public void Place_FreeLine_CountsAsZero()
    {
        var order = Order.Place(_customer, "USD", [new("GIFT", 1, 0m), new("SKU", 1, 5m)], _placedAt);

        order.Total.ShouldBe(5m);
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    [InlineData("")]
    public void Place_InvalidCurrency_Throws(string currency) =>
        Should.Throw<ArgumentException>(() => Order.Place(_customer, currency, [new("SKU", 1, 1m)], _placedAt));

    [Fact]
    public void Place_NoLines_Throws() =>
        Should.Throw<ArgumentException>(() => Order.Place(_customer, "EUR", [], _placedAt));

    [Fact]
    public void Place_TooManyLines_Throws() =>
        Should.Throw<ArgumentException>(() => Order.Place(
            _customer, "EUR", [.. Enumerable.Range(0, Order.MaxLines + 1).Select(i => new OrderLineDraft($"SKU-{i}", 1, 1m))], _placedAt));

    [Fact]
    public void Place_EmptyCustomer_Throws() =>
        Should.Throw<ArgumentException>(() => Order.Place(Guid.Empty, "EUR", [new("SKU", 1, 1m)], _placedAt));

    [Theory]
    [InlineData("SKU", 0, 1)]
    [InlineData("SKU", -1, 1)]
    [InlineData("SKU", OrderLine.MaxQuantity + 1, 1)]
    [InlineData("SKU", 1, -0.01)]
    [InlineData("SKU", 1, 0.001)]
    [InlineData("SKU", 1, 1000000000.01)]
    [InlineData(" SKU", 1, 1)]
    [InlineData("", 1, 1)]
    public void Place_InvalidLine_Throws(string sku, int quantity, double unitPrice) =>
        Should.Throw<ArgumentException>(() => Order.Place(_customer, "EUR", [new(sku, quantity, (decimal)unitPrice)], _placedAt));

    [Fact]
    public void Place_LargestOrder_TotalFitsNumeric18And2()
    {
        var lines = Enumerable.Range(0, Order.MaxLines)
            .Select(i => new OrderLineDraft($"SKU-{i}", OrderLine.MaxQuantity, OrderLine.MaxUnitPrice))
            .ToList();

        var order = Order.Place(_customer, "EUR", lines, _placedAt);

        order.Total.ShouldBe(1_000_000_000_000_000m);
        order.Total.ShouldBeLessThan(10_000_000_000_000_000m);
    }

    [Fact]
    public void ReplaceLines_Pending_ReplacesLinesAndRecomputesTotal()
    {
        var order = Order.Place(_customer, "EUR", [new("SKU-1", 1, 1m), new("SKU-2", 1, 2m)], _placedAt);
        var later = _placedAt.AddMinutes(5);

        var result = order.ReplaceLines([new("SKU-3", 4, 2.50m)], later);

        result.IsSuccess.ShouldBeTrue();
        order.Lines.Count.ShouldBe(1);
        order.Lines[0].Sku.ShouldBe("SKU-3");
        order.Lines[0].LineNumber.ShouldBe(1);
        order.Total.ShouldBe(10m);
        order.UpdatedAt.ShouldBe(later);
        order.CreatedAt.ShouldBe(_placedAt);
    }

    [Fact]
    public void Confirm_Pending_IsConfirmed()
    {
        var order = Pending();
        var later = _placedAt.AddHours(1);

        order.Confirm(later).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Confirmed);
        order.UpdatedAt.ShouldBe(later);
    }

    [Fact]
    public void Cancel_Pending_IsCancelled()
    {
        var order = Pending();

        order.Cancel(_placedAt).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Cancelled);
    }

    [Fact]
    public void Expire_Pending_IsExpired()
    {
        var order = Pending();

        order.Expire(_placedAt).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Expired);
    }

    public static TheoryData<string, string> FinalTransitions => new()
    {
        { "confirm", "confirm" }, { "confirm", "cancel" }, { "confirm", "expire" }, { "confirm", "replace" },
        { "cancel", "confirm" }, { "cancel", "cancel" }, { "cancel", "expire" }, { "cancel", "replace" },
        { "expire", "confirm" }, { "expire", "cancel" }, { "expire", "expire" }, { "expire", "replace" },
    };

    [Theory]
    [MemberData(nameof(FinalTransitions))]
    public void Transition_FromFinalStatus_IsInvalidTransition(string first, string second)
    {
        var order = Pending();
        Apply(order, first).IsSuccess.ShouldBeTrue();
        var status = order.Status;
        var total = order.Total;
        var updatedAt = order.UpdatedAt;

        var result = Apply(order, second);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(Order.InvalidTransition);
        result.Error.Code.ShouldBe("invalid_transition");
        order.Status.ShouldBe(status);
        order.Total.ShouldBe(total);
        order.UpdatedAt.ShouldBe(updatedAt);
    }

    [Fact]
    public void Delete_Twice_KeepsFirstDeletionTime()
    {
        var order = Pending();
        var first = _placedAt.AddDays(1);

        order.Delete(first);
        order.Delete(first.AddDays(1));

        order.DeletedAt.ShouldBe(first);
        order.Status.ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public void IsOwnedBy_Customer_TrueOnlyForThePlacer()
    {
        var order = Pending();

        order.IsOwnedBy(_customer).ShouldBeTrue();
        order.IsOwnedBy(Guid.CreateVersion7()).ShouldBeFalse();
    }

    private static Order Pending() => Order.Place(_customer, "EUR", [new("SKU", 1, 9.99m)], _placedAt);

    private static BuildingBlocks.Results.Result Apply(Order order, string action) => action switch
    {
        "confirm" => order.Confirm(_placedAt.AddMinutes(1)),
        "cancel" => order.Cancel(_placedAt.AddMinutes(1)),
        "expire" => order.Expire(_placedAt.AddMinutes(1)),
        "replace" => order.ReplaceLines([new("OTHER", 9, 9m)], _placedAt.AddMinutes(1)),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
