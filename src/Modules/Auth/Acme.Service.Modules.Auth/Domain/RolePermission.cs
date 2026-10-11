namespace Acme.Service.Modules.Auth.Domain;

/// <summary>A permission granted to a role, stored in <c>auth.role_permissions</c>.</summary>
internal sealed class RolePermission
{
    public RolePermission(Guid roleId, string permissionName)
    {
        RoleId = roleId;
        PermissionName = permissionName;
    }

    /// <summary>The role id.</summary>
    public Guid RoleId { get; private set; }

    /// <summary>The permission name.</summary>
    public string PermissionName { get; private set; }
}
