namespace Acme.Service.Modules.Auth.Contracts;

/// <summary>Permissions owned by the Auth module (plan §0.8). Lower-case dotted <c>&lt;resource&gt;.&lt;action&gt;</c>.</summary>
public static class AuthPermissions
{
    /// <summary>Read users.</summary>
    public const string UsersRead = "users.read";

    /// <summary>Manage users: assign and unassign roles, lock and unlock accounts.</summary>
    public const string UsersManage = "users.manage";

    /// <summary>Read roles.</summary>
    public const string RolesRead = "roles.read";
}
