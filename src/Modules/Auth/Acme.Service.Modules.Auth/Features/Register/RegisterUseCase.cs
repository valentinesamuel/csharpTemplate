using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.UseCases.Behaviors;
using Acme.Service.Modules.Auth.Contracts.IntegrationEvents;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Notifications;
using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Features.Register;

/// <summary>
/// Creates an unconfirmed account with the default role (<c>Auth:DefaultRole</c>) and writes
/// <see cref="UserRegisteredV1"/> and <see cref="EmailVerificationRequested"/> to the Auth outbox, all in the command's
/// transaction (INV-08). The result is the same for a taken email (D-23): nothing changes, an Information event is
/// logged, and a dummy password hash keeps the timing of both paths alike.
/// </summary>
internal sealed partial class RegisterUseCase(
    UserManager<AuthUser> userManager,
    [FromKeyedServices(AuthDbContext.SchemaName)] IOutbox outbox,
    PasswordTimingEqualizer timingEqualizer,
    IOptions<AuthOptions> authOptions,
    TimeProvider timeProvider,
    ILogger<RegisterUseCase> logger) : IUseCase<RegisterCommand, Done>
{
    public async Task<Result<Done>> ExecuteAsync(RegisterCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var email = input.Email.Trim();
        var existing = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (existing is not null)
        {
            timingEqualizer.VerifyAgainstDummy(input.Password);
            LogExistingAccount(logger, existing.Id);
            return Done.Value;
        }

        var now = timeProvider.GetUtcNow();
        var user = new AuthUser
        {
            UserName = email,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? null : input.DisplayName.Trim(),
            CreatedAt = now,
        };

        var created = await userManager.CreateAsync(user, input.Password).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            return ToFailure(created);
        }

        // A missing default role means the database was not migrated (the seeder creates it): fail and roll back.
        var role = authOptions.Value.DefaultRole;
        var granted = await userManager.AddToRoleAsync(user, role).ConfigureAwait(false);
        if (!granted.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not grant the default role '{role}' to a new user: {string.Join(", ", granted.Errors.Select(e => e.Code))}.");
        }

        outbox.Enqueue(new UserRegisteredV1(Guid.CreateVersion7(now), now, user.Id, email, user.DisplayName));
        outbox.Enqueue(EmailVerificationRequested.For(user.Id, timeProvider));
        LogRegistered(logger, user.Id, role);
        return Done.Value;
    }

    private Result<Done> ToFailure(IdentityResult result)
    {
        // The account appeared between the lookup and the insert: still the generic answer (D-23).
        if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
        {
            LogConcurrentRegistration(logger);
            return Done.Value;
        }

        var details = result.Errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? nameof(RegisterCommand.Password) : nameof(RegisterCommand.Email), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return Error.Validation(ValidationBehavior<RegisterCommand, Done>.ErrorCode, "One or more validation errors occurred.", details);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Registration for an existing account ignored (user {UserId}); nothing changed")]
    private static partial void LogExistingAccount(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registration ignored: the account was created concurrently; nothing changed")]
    private static partial void LogConcurrentRegistration(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} registered with role {Role}; email verification requested")]
    private static partial void LogRegistered(ILogger logger, Guid userId, string role);
}
