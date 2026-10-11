using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Web.Authorization;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Acme.Service.Modules.Auth.Features.Shared;

/// <summary>
/// Shared pieces of the RBAC admin endpoints (D-22): every one requires the <c>live-session</c> policy (D-29) and a
/// permission (§0.8).
/// </summary>
internal static class AdminHttp
{
    /// <summary>OpenAPI tag of the admin endpoints.</summary>
    public const string Tag = "Auth administration";

    /// <summary>Requires a live session and the <c>perm</c> claim <paramref name="permission"/>.</summary>
    public static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder builder, string permission) =>
        builder
            .RequireAuthorization(LiveSessionRequirement.PolicyName)
            .RequirePermission(permission)
            .WithTags(Tag)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}

/// <summary>Client-visible failures of the admin endpoints.</summary>
internal static class AdminErrors
{
    /// <summary>The role does not exist (404). Roles are code-owned (§0.8); there is no role CRUD (D-22).</summary>
    public static readonly Error RoleNotFound = Error.NotFound("role_not_found", "The role does not exist.");

    /// <summary>The change would leave no user with the Admin role (409).</summary>
    public static readonly Error LastAdmin = Error.Conflict(
        "last_admin", "The last administrator cannot lose the Admin role. Grant it to another user first.");
}
