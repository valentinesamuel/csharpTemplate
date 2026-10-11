using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.LogoutAll;

/// <summary>
/// Revokes every session of the caller (reason <c>logout_all</c>) and updates the security stamp, which also invalidates
/// outstanding email confirmation tokens, in one transaction. Access tokens already issued stay valid until they expire,
/// except on <c>live-session</c> endpoints, which now reject them (D-29).
/// </summary>
internal sealed partial class LogoutAllUseCase(
    RefreshTokenService refreshTokens,
    UserManager<AuthUser> userManager,
    ILogger<LogoutAllUseCase> logger) : IUseCase<LogoutAllCommand, Done>
{
    public const string LogoutAllEventType = "LogoutAll";

    public async Task<Result<Done>> ExecuteAsync(LogoutAllCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var revoked = await refreshTokens
            .RevokeAllSessionsAsync(input.UserId, SessionRevocationReasons.LogoutAll, cancellationToken)
            .ConfigureAwait(false);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        var user = await userManager.FindByIdAsync(input.UserId.ToString()).ConfigureAwait(false);
        if (user is not null)
        {
            var stamped = await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
            if (!stamped.Succeeded)
            {
                return Error.Conflict("concurrency_conflict", "The account was changed at the same time. Try again.");
            }
        }

        LogLoggedOutEverywhere(logger, LogoutAllEventType, input.UserId, revoked.Value);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} ended all sessions ({SessionCount} revoked)")]
    private static partial void LogLoggedOutEverywhere(ILogger logger, string eventType, Guid userId, int sessionCount);
}
