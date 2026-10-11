using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.AssignRole;

/// <summary>
/// Grants a role (D-22). Idempotent: a role the user already has is a success without a change. The new permissions
/// reach the user's access tokens at the next refresh (at most the access token lifetime, 15 minutes); sessions are not
/// revoked. 404 <c>user_not_found</c> or <c>role_not_found</c>.
/// </summary>
internal sealed partial class AssignRoleUseCase(
    UserManager<AuthUser> userManager,
    RoleManager<AuthRole> roleManager,
    ILogger<AssignRoleUseCase> logger) : IUseCase<AssignRoleCommand, Done>
{
    public const string RoleAssignedEventType = "RoleAssigned";

    public async Task<Result<Done>> ExecuteAsync(AssignRoleCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var role = await roleManager.FindByNameAsync(input.Role).ConfigureAwait(false);
        if (role?.Name is null)
        {
            return AdminErrors.RoleNotFound;
        }

        var user = await userManager.FindByIdAsync(input.UserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return RefreshTokenService.UserNotFound;
        }

        if (await userManager.IsInRoleAsync(user, role.Name).ConfigureAwait(false))
        {
            return Done.Value;
        }

        var added = await userManager.AddToRoleAsync(user, role.Name).ConfigureAwait(false);
        if (!added.Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        LogRoleAssigned(logger, RoleAssignedEventType, input.ActorId, user.Id, role.Name);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {ActorId} granted role {Role} to user {UserId}")]
    private static partial void LogRoleAssigned(ILogger logger, string eventType, Guid actorId, Guid userId, string role);
}
