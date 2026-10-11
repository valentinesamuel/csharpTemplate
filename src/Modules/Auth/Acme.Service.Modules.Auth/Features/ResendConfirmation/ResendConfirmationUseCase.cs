using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Notifications;
using Acme.Service.Modules.Auth.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.ResendConfirmation;

/// <summary>
/// Writes <see cref="EmailVerificationRequested"/> to the Auth outbox when the address belongs to an unconfirmed
/// account; otherwise does nothing. The caller always gets the same 202 (D-23).
/// </summary>
internal sealed partial class ResendConfirmationUseCase(
    UserManager<AuthUser> userManager,
    [FromKeyedServices(AuthDbContext.SchemaName)] IOutbox outbox,
    TimeProvider timeProvider,
    ILogger<ResendConfirmationUseCase> logger) : IUseCase<ResendConfirmationCommand, Done>
{
    public async Task<Result<Done>> ExecuteAsync(ResendConfirmationCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByEmailAsync(input.Email.Trim()).ConfigureAwait(false);
        if (user is null)
        {
            LogNoAccount(logger);
            return Done.Value;
        }

        if (user.EmailConfirmed)
        {
            LogAlreadyConfirmed(logger, user.Id);
            return Done.Value;
        }

        outbox.Enqueue(EmailVerificationRequested.For(user.Id, timeProvider));
        LogRequested(logger, user.Id);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Confirmation email not resent: no account has the address")]
    private static partial void LogNoAccount(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Confirmation email not resent: user {UserId} is already confirmed")]
    private static partial void LogAlreadyConfirmed(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Confirmation email requested again for user {UserId}")]
    private static partial void LogRequested(ILogger logger, Guid userId);
}
