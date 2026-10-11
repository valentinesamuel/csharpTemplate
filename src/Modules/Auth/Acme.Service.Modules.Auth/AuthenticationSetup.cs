using Acme.Service.BuildingBlocks.Web.Authorization;
using Acme.Service.Modules.Auth.Sessions;
using Acme.Service.Modules.Auth.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Acme.Service.Modules.Auth;

/// <summary>
/// Authentication and authorization for the web app, failing closed (INV-12, INV-13, INV-14):
/// <list type="bullet">
/// <item>JwtBearer is the default scheme. Tokens validate statelessly against the local key ring only: RS256,
/// <c>typ=at+jwt</c>, the configured issuer and audience, a required <c>exp</c>, a signature by a key resolved by
/// <c>kid</c>. No metadata or JWKS fetch, no events, no database or cache access.</item>
/// <item><c>FallbackPolicy = RequireAuthenticatedUser</c>: an endpoint is anonymous only through an explicit
/// <c>.AllowAnonymous()</c> (and the anonymous endpoint allowlist test).</item>
/// <item>The <c>perm:&lt;name&gt;</c> policies (BuildingBlocks.Web) read the <c>perm</c> claims of the token.</item>
/// <item>The opt-in <c>live-session</c> policy checks <c>sid</c> against <c>auth.sessions</c> and fails closed.</item>
/// </list>
/// Not used by the command hosts, which need no signing keys (§0.9).
/// </summary>
internal static class AuthenticationSetup
{
    /// <summary>The default (and only) authentication scheme.</summary>
    public const string Scheme = JwtBearerDefaults.AuthenticationScheme;

    public static IServiceCollection AddAuthenticationSetup(this IServiceCollection services, IConfiguration configuration)
    {
        JwtOptions.Register(services, configuration);
        services.TryAddSingleton<SigningKeyRing>();
        services.TryAddSingleton<AccessTokenIssuer>();
        services.TryAddScoped<RefreshTokenService>();

        services.AddAuthentication(Scheme).AddJwtBearer(Scheme, _ => { });
        services.AddOptions<JwtBearerOptions>(Scheme)
            .Configure<SigningKeyRing, IOptions<JwtOptions>, TimeProvider>(
                (bearer, keyRing, jwt, timeProvider) => ConfigureBearer(bearer, keyRing, jwt.Value, timeProvider));

        services.AddPermissionAuthorization();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(Scheme).RequireAuthenticatedUser().Build())
            .AddPolicy(LiveSessionRequirement.PolicyName, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new LiveSessionRequirement()));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, LiveSessionHandler>());
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler, LiveSessionResultHandler>());

        return services;
    }

    internal static void ConfigureBearer(JwtBearerOptions bearer, SigningKeyRing keyRing, JwtOptions jwt, TimeProvider timeProvider)
    {
        // No authority and no metadata address: nothing is ever fetched over the network (INV-13).
        bearer.Authority = null;
        bearer.MetadataAddress = string.Empty;
        bearer.MapInboundClaims = false;
        bearer.SaveToken = false;
        bearer.IncludeErrorDetails = false;
        bearer.TimeProvider = timeProvider;

        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = [SigningKeyRing.Algorithm],
            ValidTypes = [AccessTokenIssuer.TokenType],
            ClockSkew = jwt.ClockSkew,

            // Lifetime against the injected clock (TimeProvider), enforcing a required exp (RequireExpirationTime is
            // not consulted once a custom validator is set).
            LifetimeValidator = (notBefore, expires, _, parameters) =>
                IsWithinLifetime(notBefore, expires, parameters.ClockSkew, timeProvider.GetUtcNow()),

            // Keys come only from the ring, by kid; an unknown or missing kid resolves to no key (WH-15).
            IssuerSigningKeyResolver = (_, _, kid, _) => keyRing.ResolveValidationKeys(kid),
            TryAllIssuerSigningKeys = false,

            NameClaimType = AuthClaimTypes.Subject,
            RoleClaimType = AuthClaimTypes.Role,
        };
    }

    /// <summary>True when <paramref name="now"/> lies in [nbf - skew, exp + skew]; a token without <c>exp</c> is never valid.</summary>
    internal static bool IsWithinLifetime(DateTime? notBefore, DateTime? expires, TimeSpan clockSkew, DateTimeOffset now)
    {
        if (expires is null)
        {
            return false;
        }

        var utcNow = now.UtcDateTime;
        if (notBefore is not null && notBefore.Value.ToUniversalTime() > utcNow + clockSkew)
        {
            return false;
        }

        return expires.Value.ToUniversalTime() >= utcNow - clockSkew;
    }
}
