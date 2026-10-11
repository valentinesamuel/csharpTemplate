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

namespace Acme.Service.Modules.Auth.Features.ForgotPassword;

/// <summary>
/// Writes <see cref="PasswordResetRequested"/> to the Auth outbox when the address belongs to a confirmed account;
/// otherwise does nothing. The caller always gets the same 202 (D-23). The reset token is generated when the email is
/// sent, never here (INV-28).
/// </summary>
internal sealed partial class ForgotPasswordUseCase(
    UserManager<AuthUser> userManager,
    [FromKeyedServices(AuthDbContext.SchemaName)] IOutbox outbox,
    TimeProvider timeProvider,
    ILogger<ForgotPasswordUseCase> logger) : IUseCase<ForgotPasswordCommand, Done>
{
    public async Task<Result<Done>> ExecuteAsync(ForgotPasswordCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByEmailAsync(input.Email.Trim()).ConfigureAwait(false);
        if (user is null)
        {
            LogNoAccount(logger);
            return Done.Value;
        }

        if (!user.EmailConfirmed)
        {
            LogNotConfirmed(logger, user.Id);
            return Done.Value;
        }

        outbox.Enqueue(PasswordResetRequested.For(user.Id, timeProvider));
        LogRequested(logger, user.Id);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset not sent: no account has the address")]
    private static partial void LogNoAccount(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset not sent: the email address of user {UserId} is not confirmed")]
    private static partial void LogNotConfirmed(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset requested for user {UserId}")]
    private static partial void LogRequested(ILogger logger, Guid userId);
}
