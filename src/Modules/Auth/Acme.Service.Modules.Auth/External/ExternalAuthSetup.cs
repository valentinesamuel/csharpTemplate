using Acme.Service.BuildingBlocks.Web.Errors;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Acme.Service.Modules.Auth.External;

/// <summary>
/// The optional external login (D-13, D-14), registered only when <c>Auth:External:Oidc:Enabled=true</c>:
/// <list type="bullet">
/// <item>An OpenID Connect handler (scheme <c>oidc</c>): authorization code flow (<c>response_type=code</c>) with PKCE,
/// the provider's tokens are not kept (<c>SaveTokens=false</c>), and the result is signed in to Identity's external
/// cookie (<see cref="IdentityConstants.ExternalScheme"/>), never to a session of ours.</item>
/// <item>The external cookie: HttpOnly, <c>SameSite=Lax</c>, five minutes, no sliding expiration. It only carries the
/// provider's identity from the provider's redirect to <c>GET /external/oidc/callback</c>, which deletes it.</item>
/// </list>
/// JwtBearer stays the default scheme: these schemes never authenticate API calls (INV-14), and nothing here touches
/// JWT validation, which keeps fetching nothing over the network (INV-13). A provider outage fails only the external
/// login (the challenge answers 503 <c>external_provider_unavailable</c>, a failed callback redirects with
/// <c>error=external_login_failed</c>); local sign-in is unaffected.
/// </summary>
internal static class ExternalAuthSetup
{
    /// <summary>The redirect URI to register at the provider (handled by the OIDC handler, not an endpoint).</summary>
    public const string ProviderCallbackPath = AccountHttp.Prefix + "/external/oidc/signin";

    /// <summary>The <see cref="AuthenticationProperties"/> item holding the validated client return URL.</summary>
    public const string ReturnUrlItem = "returnUrl";

    /// <summary>The <c>error</c> added to the return URL when the external login failed.</summary>
    public const string LoginFailedError = "external_login_failed";

    /// <summary>The <c>error</c> added to the return URL when a local account already uses the email (D-14).</summary>
    public const string AccountExistsError = "account_exists";

    /// <summary>The cookie that carries the provider's identity to the callback.</summary>
    public const string ExternalCookieName = ".auth.external";

    /// <summary>Lifetime of the external cookie.</summary>
    public static readonly TimeSpan ExternalCookieLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Time allowed for each call to the provider (discovery, token and userinfo endpoints).</summary>
    public static readonly TimeSpan BackchannelTimeout = TimeSpan.FromSeconds(10);

    /// <summary>True when <c>Auth:External:Oidc:Enabled</c> is true.</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>($"{ExternalOidcOptions.SectionName}:{nameof(ExternalOidcOptions.Enabled)}");

    public static IServiceCollection AddExternalAuthSetup(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        ExternalOidcOptions.Register(services, configuration, allowHttpAuthority: environment.IsDevelopment());
        if (!IsEnabled(configuration))
        {
            return services;
        }

        var displayName = configuration[$"{ExternalOidcOptions.SectionName}:{nameof(ExternalOidcOptions.DisplayName)}"];
        services.AddAuthentication()
            .AddCookie(IdentityConstants.ExternalScheme, cookie => ConfigureExternalCookie(cookie, environment))
            .AddOpenIdConnect(ExternalOidcOptions.Scheme, string.IsNullOrWhiteSpace(displayName) ? null : displayName, _ => { });
        services.AddOptions<OpenIdConnectOptions>(ExternalOidcOptions.Scheme)
            .Configure<IOptions<ExternalOidcOptions>>((oidc, external) => ConfigureOidc(oidc, external.Value, environment));

        return services;
    }

    /// <summary>
    /// <paramref name="returnUrl"/> with <paramref name="name"/>=<paramref name="value"/> appended. Allowed return URLs
    /// have no query of their own (validated at startup).
    /// </summary>
    public static string AppendToReturnUrl(string returnUrl, string name, string value) =>
        QueryHelpers.AddQueryString(returnUrl, name, value);

    internal static void ConfigureOidc(OpenIdConnectOptions oidc, ExternalOidcOptions external, IHostEnvironment environment)
    {
        oidc.Authority = external.Authority;
        oidc.ClientId = external.ClientId;
        oidc.ClientSecret = external.ClientSecret;
        oidc.RequireHttpsMetadata = !environment.IsDevelopment();
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.UsePkce = true;
        oidc.SignInScheme = IdentityConstants.ExternalScheme;
        oidc.SaveTokens = false;
        oidc.GetClaimsFromUserInfoEndpoint = true;
        oidc.CallbackPath = ProviderCallbackPath;
        oidc.BackchannelTimeout = BackchannelTimeout;

        oidc.Scope.Clear();
        foreach (var scope in external.EffectiveScopes)
        {
            oidc.Scope.Add(scope);
        }

        // email_verified decides whether a provisioned account's email counts as confirmed (D-14).
        oidc.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");

        oidc.Events.OnRemoteFailure = context =>
        {
            // The provider refused or failed (or the correlation cookie is gone): back to the client page if it is
            // still allowed, otherwise a problem response. Never a 500 and never the provider's error text.
            context.HandleResponse();
            var returnUrl = context.Properties?.Items.TryGetValue(ReturnUrlItem, out var value) == true ? value : null;
            if (external.IsAllowedReturnUrl(returnUrl))
            {
                context.Response.Redirect(AppendToReturnUrl(returnUrl!, "error", LoginFailedError));
                return Task.CompletedTask;
            }

            return Results.Problem(
                    detail: "The external sign-in failed.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { [ProblemDetailsSetup.CodeKey] = LoginFailedError })
                .ExecuteAsync(context.HttpContext);
        };
    }

    private static void ConfigureExternalCookie(CookieAuthenticationOptions cookie, IHostEnvironment environment)
    {
        cookie.Cookie.Name = ExternalCookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        cookie.ExpireTimeSpan = ExternalCookieLifetime;
        cookie.SlidingExpiration = false;

        // This cookie never authorizes anything; no redirects to login or access-denied pages.
        cookie.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        cookie.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }
}
