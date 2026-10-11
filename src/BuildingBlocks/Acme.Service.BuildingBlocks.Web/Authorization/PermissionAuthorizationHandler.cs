using Microsoft.AspNetCore.Authorization;

namespace Acme.Service.BuildingBlocks.Web.Authorization;

/// <summary>
/// Succeeds iff an authenticated identity of the user has a <c>perm</c> claim equal (ordinal) to the
/// required permission. Stateless: reads claims only (INV-13). Never fails other handlers explicitly.
/// </summary>
internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        foreach (var identity in context.User.Identities)
        {
            if (identity.IsAuthenticated && identity.HasClaim(c =>
                string.Equals(c.Type, PermissionRequirement.ClaimType, StringComparison.Ordinal)
                && string.Equals(c.Value, requirement.Permission, StringComparison.Ordinal)))
            {
                context.Succeed(requirement);
                break;
            }
        }

        return Task.CompletedTask;
    }
}
