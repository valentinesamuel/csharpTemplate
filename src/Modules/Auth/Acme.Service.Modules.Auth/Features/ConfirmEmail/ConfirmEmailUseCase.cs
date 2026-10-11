using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.ConfirmEmail;

/// <summary>
/// Confirms the email address with Identity's email confirmation token (lifespan <c>Auth:Identity:TokenLifespan</c>,
/// invalidated by a security stamp change). An unknown user and a bad token fail alike with 400 <c>invalid_token</c>.
/// Confirming an already confirmed address with a valid token succeeds again.
/// </summary>
internal sealed partial class ConfirmEmailUseCase(
    UserManager<AuthUser> userManager,
    ILogger<ConfirmEmailUseCase> logger) : IUseCase<ConfirmEmailCommand, Done>
{
    public const string EmailConfirmedEventType = "EmailConfirmed";

    public async Task<Result<Done>> ExecuteAsync(ConfirmEmailCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByIdAsync(input.UserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return AccountErrors.InvalidToken;
        }

        var confirmed = await userManager.ConfirmEmailAsync(user, input.Token).ConfigureAwait(false);
        if (!confirmed.Succeeded)
        {
            LogConfirmationRejected(logger, user.Id);
            return AccountErrors.InvalidToken;
        }

        LogEmailConfirmed(logger, EmailConfirmedEventType, user.Id);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} confirmed the email address")]
    private static partial void LogEmailConfirmed(ILogger logger, string eventType, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email confirmation for user {UserId} rejected: invalid or expired token")]
    private static partial void LogConfirmationRejected(ILogger logger, Guid userId);
}
