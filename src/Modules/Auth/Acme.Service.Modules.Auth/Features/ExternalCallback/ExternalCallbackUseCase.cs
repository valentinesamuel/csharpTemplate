using System.Security.Claims;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Contracts.IntegrationEvents;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.External;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Features.ExternalCallback;

/// <summary>
/// The external login decision (D-14):
/// <list type="number">
/// <item>A user linked to the provider's key (<c>auth.user_logins</c>) signs in.</item>
/// <item>No link and no local user with the provider's email: a new user is provisioned with that email (confirmed only
/// when the provider says <c>email_verified=true</c>), no password, the login link and <c>Auth:DefaultRole</c>; and
/// <see cref="UserRegisteredV1"/> is written to the outbox, as for a self-registration.</item>
/// <item>No link but a local user has the email: <see cref="ExternalCallbackOutcome.AccountExists"/>, nothing changes.
/// Never auto-linked: a provider asserting someone else's email must not take over their account.</item>
/// </list>
/// A signed-in or provisioned user gets a one-time code (<see cref="ExternalLoginCode"/>, 60 seconds, stored hashed).
/// No email from the provider, a locked user, or an account Identity rejects fails with <c>external_login_failed</c>.
/// Logged as <c>SecurityEvent</c>s with ids and the provider name only; never the email, the provider key or the code.
/// </summary>
internal sealed partial class ExternalCallbackUseCase(
    AuthDbContext context,
    UserManager<AuthUser> userManager,
    [FromKeyedServices(AuthDbContext.SchemaName)] IOutbox outbox,
    IOptions<AuthOptions> authOptions,
    TimeProvider timeProvider,
    ILogger<ExternalCallbackUseCase> logger) : IUseCase<ExternalCallbackCommand, ExternalCallbackResult>
{
    public const string ExternalLoginSucceededEventType = "ExternalLoginSucceeded";

    public const string ExternalLoginFailedEventType = "ExternalLoginFailed";

    /// <summary>The provider's email verification claim (OpenID Connect Core, standard claims).</summary>
    public const string EmailVerifiedClaim = "email_verified";

    public async Task<Result<ExternalCallbackResult>> ExecuteAsync(ExternalCallbackCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var login = input.Login;
        var outcome = ExternalCallbackOutcome.SignedIn;
        var user = await userManager.FindByLoginAsync(login.LoginProvider, login.ProviderKey).ConfigureAwait(false);
        if (user is null)
        {
            var email = EmailOf(login.Principal);
            if (email is null)
            {
                LogExternalLoginFailed(logger, ExternalLoginFailedEventType, login.LoginProvider, null, "no_email");
                return ExternalErrors.LoginFailed;
            }

            if (await userManager.FindByEmailAsync(email).ConfigureAwait(false) is { } existing)
            {
                LogAccountExists(logger, login.LoginProvider, existing.Id);
                return ExternalCallbackResult.AccountExists;
            }

            var provisioned = await ProvisionAsync(login, email).ConfigureAwait(false);
            if (provisioned.IsFailure)
            {
                return provisioned.Error;
            }

            if (provisioned.Value is null)
            {
                // The email was taken concurrently: the same answer as an existing account.
                LogAccountExists(logger, login.LoginProvider, null);
                return ExternalCallbackResult.AccountExists;
            }

            user = provisioned.Value;
            outcome = ExternalCallbackOutcome.Provisioned;
        }
        else if (await userManager.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            LogExternalLoginFailed(logger, ExternalLoginFailedEventType, login.LoginProvider, user.Id, "locked_out");
            return ExternalErrors.LoginFailed;
        }

        var now = timeProvider.GetUtcNow();
        await context.ExternalLoginCodes
            .Where(c => c.UserId == user.Id && (c.ExpiresAt <= now || c.ConsumedAt != null))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        var (code, hash) = ExternalLoginCode.NewCode();
        context.ExternalLoginCodes.Add(new ExternalLoginCode(user.Id, hash, now));

        LogExternalLoginSucceeded(logger, ExternalLoginSucceededEventType, login.LoginProvider, user.Id, outcome);
        return new ExternalCallbackResult(outcome, user.Id, code);
    }

    /// <summary>The provider's email, trimmed, or null when it has none.</summary>
    internal static string? EmailOf(ClaimsPrincipal principal)
    {
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    /// <summary>True only when the provider asserts <c>email_verified=true</c>.</summary>
    internal static bool IsEmailVerified(ClaimsPrincipal principal) =>
        bool.TryParse(principal.FindFirstValue(EmailVerifiedClaim), out var verified) && verified;

    private static string? DisplayNameOf(ClaimsPrincipal principal)
    {
        var name = principal.FindFirstValue(ClaimTypes.Name) ?? principal.FindFirstValue("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim();
        return name.Length <= AuthUser.DisplayNameMaxLength ? name : name[..AuthUser.DisplayNameMaxLength];
    }

    /// <summary>The new user, or null when the email or user name was taken concurrently.</summary>
    private async Task<Result<AuthUser?>> ProvisionAsync(ExternalLoginInfo login, string email)
    {
        var now = timeProvider.GetUtcNow();
        var user = new AuthUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = IsEmailVerified(login.Principal),
            DisplayName = DisplayNameOf(login.Principal),
            CreatedAt = now,
        };

        var created = await userManager.CreateAsync(user).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
            {
                return Result.Success<AuthUser?>(null);
            }

            LogExternalLoginFailed(
                logger, ExternalLoginFailedEventType, login.LoginProvider, null, string.Join(",", created.Errors.Select(e => e.Code)));
            return ExternalErrors.LoginFailed;
        }

        var linked = await userManager
            .AddLoginAsync(user, new UserLoginInfo(login.LoginProvider, login.ProviderKey, login.ProviderDisplayName))
            .ConfigureAwait(false);
        if (!linked.Succeeded)
        {
            LogExternalLoginFailed(
                logger, ExternalLoginFailedEventType, login.LoginProvider, user.Id, string.Join(",", linked.Errors.Select(e => e.Code)));
            return ExternalErrors.LoginFailed;
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
        return user;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "SecurityEvent {EventType}: user {UserId} signed in with external provider {Provider} ({Outcome}); one-time code issued")]
    private static partial void LogExternalLoginSucceeded(ILogger logger, string eventType, string provider, Guid userId, ExternalCallbackOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SecurityEvent {EventType}: external sign-in with provider {Provider} failed for user {UserId} ({Reason})")]
    private static partial void LogExternalLoginFailed(ILogger logger, string eventType, string provider, Guid? userId, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SecurityEvent ExternalLoginAccountExists: provider {Provider} asserted the email of local user {UserId}, which is not linked; not linked automatically")]
    private static partial void LogAccountExists(ILogger logger, string provider, Guid? userId);
}
