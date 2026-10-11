using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.UnassignRole;

/// <summary>
/// Removes a role (D-22). Idempotent: a role the user does not have is a success without a change. Removing Admin is
/// refused with 409 <c>last_admin</c> when no other unlocked user would keep it; the check runs under a row lock on the
/// Admin role (<c>SELECT ... FOR UPDATE</c>, inside the command's transaction), so two concurrent removals cannot both
/// pass it. The user's tokens lose the permissions at the next refresh (at most 15 minutes). 404 <c>user_not_found</c>
/// or <c>role_not_found</c>.
/// </summary>
internal sealed partial class UnassignRoleUseCase(
    AuthDbContext context,
    UserManager<AuthUser> userManager,
    RoleManager<AuthRole> roleManager,
    TimeProvider timeProvider,
    ILogger<UnassignRoleUseCase> logger) : IUseCase<UnassignRoleCommand, Done>
{
    public const string RoleUnassignedEventType = "RoleUnassigned";

    public async Task<Result<Done>> ExecuteAsync(UnassignRoleCommand input, CancellationToken cancellationToken)
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

        if (string.Equals(role.Name, AuthRole.Admin, StringComparison.Ordinal))
        {
            // Serialise every Admin removal on the role row until the transaction ends.
            await context.Database
                .ExecuteSqlAsync($"SELECT 1 FROM auth.roles WHERE id = {role.Id} FOR UPDATE", cancellationToken)
                .ConfigureAwait(false);
        }

        if (!await userManager.IsInRoleAsync(user, role.Name).ConfigureAwait(false))
        {
            return Done.Value;
        }

        if (string.Equals(role.Name, AuthRole.Admin, StringComparison.Ordinal)
            && !await AnotherActiveAdminExistsAsync(role.Id, user.Id, cancellationToken).ConfigureAwait(false))
        {
            LogLastAdminRefused(logger, input.ActorId, user.Id);
            return AdminErrors.LastAdmin;
        }

        var removed = await userManager.RemoveFromRoleAsync(user, role.Name).ConfigureAwait(false);
        if (!removed.Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        LogRoleUnassigned(logger, RoleUnassignedEventType, input.ActorId, user.Id, role.Name);
        return Done.Value;
    }

    private async Task<bool> AnotherActiveAdminExistsAsync(Guid adminRoleId, Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await (
                from userRole in context.UserRoles
                join other in context.Users on userRole.UserId equals other.Id
                where userRole.RoleId == adminRoleId
                    && userRole.UserId != userId
                    && (other.LockoutEnd == null || other.LockoutEnd <= now)
                select userRole.UserId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {ActorId} removed role {Role} from user {UserId}")]
    private static partial void LogRoleUnassigned(ILogger logger, string eventType, Guid actorId, Guid userId, string role);

    [LoggerMessage(Level = LogLevel.Warning, Message = "User {ActorId} tried to remove the Admin role from user {UserId}, the last active administrator; refused")]
    private static partial void LogLastAdminRefused(ILogger logger, Guid actorId, Guid userId);
}
