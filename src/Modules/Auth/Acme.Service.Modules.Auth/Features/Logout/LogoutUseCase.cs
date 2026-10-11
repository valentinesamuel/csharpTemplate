using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.Logout;

/// <summary>
/// Revokes the caller's session identified by a refresh token (reason <c>logout</c>). A malformed or unknown token,
/// another user's session or an already ended one change nothing and still succeed, so the response never reveals
/// which; only a database outage fails (503).
/// </summary>
internal sealed partial class LogoutUseCase(
    RefreshTokenService refreshTokens,
    ILogger<LogoutUseCase> logger) : IUseCase<LogoutCommand, Done>
{
    public const string LogoutEventType = "Logout";

    public async Task<Result<Done>> ExecuteAsync(LogoutCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var revoked = await refreshTokens
            .RevokeSessionByRefreshTokenAsync(input.RefreshToken, input.UserId, SessionRevocationReasons.Logout, cancellationToken)
            .ConfigureAwait(false);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        if (revoked.Value)
        {
            LogLoggedOut(logger, LogoutEventType, input.UserId);
        }

        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} ended a session")]
    private static partial void LogLoggedOut(ILogger logger, string eventType, Guid userId);
}
