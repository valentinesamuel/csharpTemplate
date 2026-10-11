using Acme.Service.Modules.Auth.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Cli;

/// <summary>Outcome of <see cref="GrantRoleCommand"/>.</summary>
internal enum GrantRoleOutcome
{
    Granted,
    AlreadyGranted,
    UserNotFound,
    RoleNotFound,
    Failed,
}

/// <summary>
/// <c>auth:grant-role --email &lt;e&gt; --role &lt;r&gt;</c> (§0.8, §0.9): the bootstrap path for the first admin. Idempotent:
/// granting a role the user already has succeeds without a change. Never creates users or roles.
/// </summary>
internal sealed partial class GrantRoleCommand(
    UserManager<AuthUser> userManager,
    RoleManager<AuthRole> roleManager,
    ILogger<GrantRoleCommand> logger)
{
    public const string Verb = "auth:grant-role";

    /// <summary><c>EventType</c> of the security event logged on a grant.</summary>
    public const string RoleGrantedEventType = "auth.role-granted";

    public async Task<GrantRoleOutcome> ExecuteAsync(string email, string roleName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        cancellationToken.ThrowIfCancellationRequested();

        var role = await roleManager.FindByNameAsync(roleName).ConfigureAwait(false);
        if (role?.Name is null)
        {
            LogRoleNotFound(logger, roleName);
            return GrantRoleOutcome.RoleNotFound;
        }

        // The email is not logged (personal data); the operator already has it.
        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            LogUserNotFound(logger);
            return GrantRoleOutcome.UserNotFound;
        }

        if (await userManager.IsInRoleAsync(user, role.Name).ConfigureAwait(false))
        {
            LogAlreadyGranted(logger, user.Id, role.Name);
            return GrantRoleOutcome.AlreadyGranted;
        }

        var result = await userManager.AddToRoleAsync(user, role.Name).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            LogFailed(logger, user.Id, role.Name, string.Join(", ", result.Errors.Select(e => e.Code)));
            return GrantRoleOutcome.Failed;
        }

        LogGranted(logger, RoleGrantedEventType, user.Id, role.Name);
        return GrantRoleOutcome.Granted;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Role {Role} does not exist")]
    private static partial void LogRoleNotFound(ILogger logger, string role);

    [LoggerMessage(Level = LogLevel.Error, Message = "No user has the given email")]
    private static partial void LogUserNotFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} already has role {Role}")]
    private static partial void LogAlreadyGranted(ILogger logger, Guid userId, string role);

    [LoggerMessage(Level = LogLevel.Error, Message = "Granting role {Role} to user {UserId} failed: {ErrorCodes}")]
    private static partial void LogFailed(ILogger logger, Guid userId, string role, string errorCodes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SecurityEvent {EventType}: granted role {Role} to user {UserId}")]
    private static partial void LogGranted(ILogger logger, string eventType, Guid userId, string role);
}
