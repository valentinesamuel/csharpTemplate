using Microsoft.AspNetCore.Identity;

namespace Acme.Service.Modules.Auth.Domain;

/// <summary>An Identity role with a Guid v7 key, stored in <c>auth.roles</c>.</summary>
internal sealed class AuthRole : IdentityRole<Guid>
{
    /// <summary>Administrators: every permission (plan §0.8).</summary>
    public const string Admin = "Admin";

    /// <summary>Staff: read and operational permissions.</summary>
    public const string Staff = "Staff";

    /// <summary>Customers: the default role of self-registered users.</summary>
    public const string Customer = "Customer";

    /// <summary>The seed roles, re-synced by every <c>migrate</c> run.</summary>
    public static readonly IReadOnlyList<string> SeedRoles = [Admin, Staff, Customer];

    public AuthRole()
    {
        Id = Guid.CreateVersion7();
    }

    public AuthRole(string roleName)
        : this()
    {
        Name = roleName;
    }

    /// <summary>The permissions granted to this role.</summary>
    public ICollection<RolePermission> Permissions { get; } = [];
}
