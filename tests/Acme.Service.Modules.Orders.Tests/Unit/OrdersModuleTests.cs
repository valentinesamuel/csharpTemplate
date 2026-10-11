using System.Reflection;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Contracts.IntegrationEvents;
using Acme.Service.Modules.Orders.Features.ListOrders;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Unit;

public sealed class OrdersModuleTests
{
    [Fact]
    public void Module_NameAndPermissions_MatchThePermissionTable()
    {
        var module = new OrdersModule();

        module.Name.ShouldBe("orders");
        var roles = module.Permissions.ToDictionary(p => p.Name, p => p.DefaultRoles.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        roles.Keys.ShouldBe(["orders.read", "orders.create", "orders.cancel", "orders.manage"], ignoreOrder: true);
        roles[OrdersPermissions.Read].ShouldBe(["Admin", "Customer", "Staff"]);
        roles[OrdersPermissions.Create].ShouldBe(["Admin", "Customer"]);
        roles[OrdersPermissions.Cancel].ShouldBe(["Admin", "Customer"]);
        roles[OrdersPermissions.Manage].ShouldBe(["Admin", "Staff"]);
        module.Permissions.ShouldAllBe(p => !string.IsNullOrWhiteSpace(p.Description));
    }

    [Fact]
    public void OrderPlacedV1_StableName_IsDeclared() =>
        IntegrationEventAttribute.GetName(typeof(OrderPlacedV1)).ShouldBe("orders.order-placed.v1");

    [Fact]
    public void Assembly_PublicTypes_AreOnlyTheModule()
    {
        var publicTypes = typeof(OrdersModule).Assembly.GetExportedTypes();

        publicTypes.ShouldBe([typeof(OrdersModule)]);
    }

    [Fact]
    public void Assembly_NonPersistenceTypes_DoNotReferenceEntityFramework()
    {
        // INV-04: EF types appear only in the Persistence namespace (P18 adds the architecture test).
        var offenders = typeof(OrdersModule).Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && !ns.StartsWith("Acme.Service.Modules.Orders.Persistence", StringComparison.Ordinal))
            .Where(ReferencesEntityFramework)
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void ListSort_Whitelist_IsCreatedAtTotalStatus()
    {
        ListOrdersSort.Map.Fields.ShouldBe(["createdAt", "total", "status"], ignoreOrder: true);
        ListOrdersSort.Map.Resolve("-createdAt,total").IsSuccess.ShouldBeTrue();
        var unknown = ListOrdersSort.Map.Resolve("customerId");
        unknown.IsFailure.ShouldBeTrue();
        unknown.Error.Code.ShouldBe("invalid_sort");
    }

    private static bool ReferencesEntityFramework(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        IEnumerable<Type> used = [
            .. type.GetFields(all).Select(f => f.FieldType),
            .. type.GetProperties(all).Select(p => p.PropertyType),
            .. type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)),
            .. type.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType)),
            .. type.GetInterfaces(),
        ];
        if (type.BaseType is { } baseType)
        {
            used = used.Append(baseType);
        }

        return used.Any(IsEntityFramework);
    }

    private static bool IsEntityFramework(Type type) =>
        (type.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ?? false)
        || (type.IsGenericType && type.GetGenericArguments().Any(IsEntityFramework));
}
