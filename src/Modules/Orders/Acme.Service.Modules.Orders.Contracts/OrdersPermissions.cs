using Acme.Service.BuildingBlocks.Security;

namespace Acme.Service.Modules.Orders.Contracts;

/// <summary>Permissions owned by the Orders module (plan §0.8). Lower-case dotted <c>&lt;resource&gt;.&lt;action&gt;</c>.</summary>
public static class OrdersPermissions
{
    /// <summary>Read own orders; with <see cref="Manage"/>, read every order.</summary>
    public const string Read = "orders.read";

    /// <summary>Place orders and change the lines of own pending orders.</summary>
    public const string Create = "orders.create";

    /// <summary>Cancel own pending orders.</summary>
    public const string Cancel = "orders.cancel";

    /// <summary>Manage every order: read, change, confirm, cancel and delete.</summary>
    public const string Manage = "orders.manage";

    // Seed role names (§0.8). Contracts reference only BuildingBlocks (§0.3), so the Auth role names are repeated here;
    // the Auth seeder rejects a name that is not a seed role.
    private const string Admin = "Admin";
    private const string Staff = "Staff";
    private const string Customer = "Customer";

    /// <summary>The Orders permissions with their seed roles (§0.8), declared through <c>IModule.Permissions</c>.</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Read, "Read own orders; with orders.manage, read every order.", [Admin, Staff, Customer]),
        new(Create, "Place orders and change the lines of own pending orders.", [Admin, Customer]),
        new(Cancel, "Cancel own pending orders.", [Admin, Customer]),
        new(Manage, "Manage every order: read, change, confirm, cancel and delete.", [Admin, Staff]),
    ];
}
