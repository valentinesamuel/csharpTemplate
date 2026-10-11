using System.Security.Claims;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.CreateOrder;
using Acme.Service.Modules.Orders.Features.ListOrders;
using Acme.Service.Modules.Orders.Features.Shared;
using Acme.Service.Modules.Orders.Features.UpdateOrderLines;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Unit;

public sealed class OrderValidationTests
{
    private static readonly OrderLineRequest _line = new("SKU-1", 1, 9.99m);

    [Fact]
    public void CreateValidator_ValidCommand_Passes() =>
        new CreateOrderValidator().Validate(new CreateOrderCommand(Guid.CreateVersion7(), "EUR", [_line])).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("eur")]
    [InlineData("EURO")]
    public void CreateValidator_InvalidCurrency_FailsOnCurrency(string? currency)
    {
        var result = new CreateOrderValidator().Validate(new CreateOrderCommand(Guid.CreateVersion7(), currency, [_line]));

        result.Errors.Select(e => e.PropertyName).ShouldBe(["Currency"]);
    }

    [Fact]
    public void CreateValidator_NoLines_FailsOnLines()
    {
        var missing = new CreateOrderValidator().Validate(new CreateOrderCommand(Guid.CreateVersion7(), "EUR", null));
        var empty = new CreateOrderValidator().Validate(new CreateOrderCommand(Guid.CreateVersion7(), "EUR", []));

        missing.Errors.Select(e => e.PropertyName).ShouldBe(["Lines"]);
        empty.Errors.Select(e => e.PropertyName).ShouldBe(["Lines"]);
    }

    [Fact]
    public void CreateValidator_InvalidLineFields_FailsPerField()
    {
        var result = new CreateOrderValidator().Validate(new CreateOrderCommand(
            Guid.CreateVersion7(), "EUR", [_line, new OrderLineRequest(null, 0, 1.234m), null!]));

        result.Errors.Select(e => e.PropertyName).ShouldBe(
            ["Lines[1].Sku", "Lines[1].Quantity", "Lines[1].UnitPrice", "Lines[2]"], ignoreOrder: true);
    }

    [Fact]
    public void UpdateValidator_MissingVersion_FailsOnVersion()
    {
        var caller = new OrdersCaller(Guid.CreateVersion7(), CanManage: false);

        var result = new UpdateOrderLinesValidator().Validate(new UpdateOrderLinesCommand(Guid.CreateVersion7(), caller, null, [_line]));

        result.Errors.Select(e => e.PropertyName).ShouldBe(["Version"]);
    }

    [Fact]
    public void BindFilters_ValidValues_ParseToUtc()
    {
        var result = ListOrdersEndpoint.BindFilters(Query(
            ("status", "confirmed"), ("createdFrom", "2026-01-01T01:00:00+01:00"), ("createdTo", "2026-02-01")));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(OrderStatus.Confirmed);
        result.Value.CreatedFrom.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        result.Value.CreatedFrom!.Value.Offset.ShouldBe(TimeSpan.Zero);
        result.Value.CreatedTo.ShouldBe(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void BindFilters_None_AreNull()
    {
        var result = ListOrdersEndpoint.BindFilters(Query());

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new ListOrdersFilters(null, null, null));
    }

    [Theory]
    [InlineData("status", "1")]
    [InlineData("status", "Shipped")]
    [InlineData("createdFrom", "yesterday")]
    [InlineData("createdTo", "2026-13-01")]
    public void BindFilters_InvalidValue_IsInvalidFilter(string name, string value)
    {
        var result = ListOrdersEndpoint.BindFilters(Query((name, value)));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("invalid_filter");
        result.Error.Details.ShouldNotBeNull().Keys.ShouldBe([name]);
    }

    [Fact]
    public void BindFilters_RepeatedValue_IsInvalidFilter()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["status"] = new(["Pending", "Confirmed"]),
        });

        ListOrdersEndpoint.BindFilters(query).Error.Code.ShouldBe("invalid_filter");
    }

    [Fact]
    public void ListValidator_CreatedToBeforeCreatedFrom_Fails()
    {
        var caller = new OrdersCaller(Guid.CreateVersion7(), CanManage: false);
        var from = DateTimeOffset.UtcNow;

        var result = new ListOrdersValidator().Validate(new ListOrdersQuery(
            caller, null, from, from.AddSeconds(-1), BuildingBlocks.Paging.PageRequest.Default, []));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Caller_FromClaims_ReadsSubjectAndManage()
    {
        var userId = Guid.CreateVersion7();
        var staff = Principal(("sub", userId.ToString()), ("perm", "orders.read"), ("perm", "orders.manage"));
        var customer = Principal(("sub", userId.ToString()), ("perm", "orders.read"));
        var noSubject = Principal(("perm", "orders.manage"));

        OrdersCaller.TryFrom(staff, out var staffCaller).ShouldBeTrue();
        staffCaller.ShouldBe(new OrdersCaller(userId, CanManage: true));
        OrdersCaller.TryFrom(customer, out var customerCaller).ShouldBeTrue();
        customerCaller!.CanManage.ShouldBeFalse();
        customerCaller.MayAccess(userId).ShouldBeTrue();
        customerCaller.MayAccess(Guid.CreateVersion7()).ShouldBeFalse();
        staffCaller!.MayAccess(Guid.CreateVersion7()).ShouldBeTrue();
        OrdersCaller.TryFrom(noSubject, out _).ShouldBeFalse();
    }

    private static QueryCollection Query(params (string Name, string Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => new StringValues(v.Value), StringComparer.Ordinal));

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));
}
