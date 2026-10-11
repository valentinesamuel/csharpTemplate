using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Acme.Service.BuildingBlocks.Security;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Modules.Auth.Cli;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Contracts.IntegrationEvents;
using Acme.Service.Modules.Auth.Directory;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.External;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Mfa;
using Acme.Service.Modules.Auth.Notifications;
using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.RateLimiting;
using Acme.Service.Modules.Auth.Seeding;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.Modules.Auth;

/// <summary>
/// The Auth module (schema <c>auth</c>): ASP.NET Core Identity on EF Core, the permission catalog and seed roles,
/// the shared Data Protection key ring, the <c>auth:grant-role</c> bootstrap command, and the token mechanics (JWT
/// signing key ring, access tokens, JWKS, sessions and refresh tokens) with the fail-closed authorization setup, and the
/// account endpoints under <c>/api/v1/auth</c> with their rate limiting policies, confirmation and password reset email
/// flows, TOTP two-factor authentication, the RBAC admin endpoints and the optional external OpenID Connect login. The
/// only public type of the assembly (INV-02).
/// </summary>
public sealed class AuthModule : IModule
{
    /// <summary>The <c>auth:grant-role</c> verb.</summary>
    public const string GrantRoleVerb = GrantRoleCommand.Verb;

    /// <inheritdoc />
    public string Name => AuthDbContext.SchemaName;

    /// <inheritdoc />
    public IReadOnlyList<PermissionDefinition> Permissions => PermissionCatalog.AuthPermissionDefinitions;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(configuration);

        services.AddIntegrationEvents(typeof(UserRegisteredV1).Assembly);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IUserDirectory, UserDirectory>();

        // JWT bearer (default scheme), the key ring, token issuance, sessions, the fail-closed fallback policy and the
        // live-session policy (P12). Web app only: the command hosts call AddPersistence alone and need no keys.
        services.AddAuthenticationSetup(configuration);

        // Account endpoints (P13): options, timing equalisation, rate limiting policies, and the email flow
        // outbox -> EmailVerificationRequestedHandler (inbox) -> SendEmailVerificationJob -> INotificationSender.
        AuthOptions.Register(services, configuration);
        AuthLinksOptions.Register(services, configuration);
        services.TryAddSingleton<PasswordTimingEqualizer>();
        services.AddAuthRateLimiting(configuration);
        services.AddIntegrationEventTypes(typeof(EmailVerificationRequested));
        services.AddIntegrationEventHandler<EmailVerificationRequested, EmailVerificationRequestedHandler>(
            Name, EmailVerificationRequestedHandler.ConsumerName);
        services.TryAddScoped<SendEmailVerificationJob>();

        // Password lifecycle and TOTP MFA (P14): the reset email flow
        // outbox -> PasswordResetRequestedHandler (inbox) -> SendPasswordResetJob -> INotificationSender, the mfa token
        // and challenges, and the TOTP verification with replay protection.
        AuthMfaOptions.Register(services, configuration);
        services.AddIntegrationEventTypes(typeof(PasswordResetRequested));
        services.AddIntegrationEventHandler<PasswordResetRequested, PasswordResetRequestedHandler>(
            Name, PasswordResetRequestedHandler.ConsumerName);
        services.TryAddScoped<SendPasswordResetJob>();
        services.TryAddScoped<MfaTokenIssuer>();
        services.TryAddScoped<TotpReplayGuard>();

        // RBAC admin endpoints (P15) need no services of their own. The external OIDC login (P15, D-13) is registered
        // only when Auth:External:Oidc:Enabled=true; its options are validated on start either way (INV-17).
        services.AddExternalAuthSetup(configuration, environment);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Identity is registered here rather than in <see cref="AddServices"/> because the minimal hosts (<c>migrate</c>,
    /// <c>auth:grant-role</c>) call only this method, and <c>auth:grant-role</c> needs the user and role managers.
    /// </remarks>
    public void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<AuthDbContext>(configuration, Name);

        // WH-12/WH-13: Identity core with Guid keys and no cookie schemes; MapIdentityApi is not used.
        AuthIdentityOptions.Register(services, configuration);
        services.AddIdentityCore<AuthUser>()
            .AddRoles<AuthRole>()
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddDefaultTokenProviders()
            .AddSignInManager();

        // SignInManager needs the authentication core services (IAuthenticationSchemeProvider), even though no cookie
        // scheme is registered (WH-12). The JwtBearer scheme is added by AddServices (web app only).
        services.AddAuthentication();

        // D-15: the store that encrypts the authenticator key and hashes recovery codes.
        services.Replace(ServiceDescriptor.Scoped<IUserStore<AuthUser>, AuthUserStore>());

        // D-27: one key ring for every replica, in auth.data_protection_keys. The Host sets the application name.
        services.AddDataProtection().PersistKeysToDbContext<AuthDbContext>();

        services.AddPostMigrationStep<AuthSeeder>();
        services.AddScoped<GrantRoleCommand>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No module-level routes; feature endpoints are discovered as IEndpoint implementations.
    }

    /// <summary>
    /// Runs <c>auth:grant-role</c> against <paramref name="services"/>, a provider built with
    /// <see cref="AddPersistence"/>. Idempotent.
    /// </summary>
    /// <returns>0 when the user has the role afterwards; 1 when the user or the role does not exist, or the grant fails.</returns>
    public static async Task<int> GrantRoleAsync(IServiceProvider services, string email, string role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var command = scope.ServiceProvider.GetRequiredService<GrantRoleCommand>();
            var outcome = await command.ExecuteAsync(email, role, cancellationToken).ConfigureAwait(false);
            return outcome is GrantRoleOutcome.Granted or GrantRoleOutcome.AlreadyGranted ? 0 : 1;
        }
    }
}
