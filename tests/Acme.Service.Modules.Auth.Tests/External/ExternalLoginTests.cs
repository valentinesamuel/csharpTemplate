using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.External;
using Acme.Service.Modules.Auth.Features.ExternalCallback;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Modules.Auth.Tokens;
using Acme.Service.Testing;
using Acme.Service.Testing.Containers;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.External;

/// <summary>
/// The external OIDC slot (D-13, D-14): off by default (not mapped), exact-match return URLs, the callback decision
/// (provision, account_exists, existing link) run with a fake <see cref="ExternalLoginInfo"/>, and the single-use,
/// 60-second exchange code. No identity provider is contacted.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class ExternalLoginTests(PostgresFixture postgres)
{
    private const string Prefix = "/api/v1/auth";

    private const string ReturnUrl = "https://app.example.test/auth/callback";

    private const string Authority = "https://idp.example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The settings of an enabled external login.</summary>
    internal static Dictionary<string, string?> EnabledSettings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Auth:External:Oidc:Enabled"] = "true",
        ["Auth:External:Oidc:DisplayName"] = "Test provider",
        ["Auth:External:Oidc:Authority"] = Authority,
        ["Auth:External:Oidc:ClientId"] = "service-client",
        ["Auth:External:Oidc:ClientSecret"] = "test-client-secret-value",
        ["Auth:External:Oidc:AllowedReturnUrls:0"] = ReturnUrl,
    };

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Disabled_ByDefault_EndpointsAndSchemesAreAbsent()
    {
        await using var factory = new ServiceWebFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Prefix}/external/oidc/challenge?returnUrl={Uri.EscapeDataString(ReturnUrl)}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken(factory));

        using var challenge = await client.SendAsync(request, Ct);

        // Authenticated, the route simply does not exist (anonymous callers get the fallback policy's 401 first, WH-16).
        challenge.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var patterns = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? string.Empty).ToList();
        patterns.ShouldNotContain(p => p.Contains("/external/", StringComparison.Ordinal));
        var schemes = await factory.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        schemes.Select(s => s.Name).ShouldNotContain(ExternalOidcOptions.Scheme);
        schemes.Select(s => s.Name).ShouldNotContain(IdentityConstants.ExternalScheme);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Challenge_AllowedReturnUrl_RedirectsToProviderWithCodeFlowAndPkce()
    {
        // The handler protects its state with the Data Protection key ring, which lives in the Auth database.
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await using var factory = new ExternalWebFactory(EnabledSettings(), connectionString: connectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var challenge = await client.GetAsync(new Uri($"{Prefix}/external/oidc/challenge?returnUrl={Uri.EscapeDataString(ReturnUrl)}", UriKind.Relative), Ct);

        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect, await challenge.Content.ReadAsStringAsync(Ct));
        var location = challenge.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).ShouldBe($"{Authority}/authorize");
        var query = QueryHelpers.ParseQuery(location.Query);
        query["response_type"].ToString().ShouldBe("code");
        query["code_challenge_method"].ToString().ShouldBe("S256");
        query["code_challenge"].ToString().ShouldNotBeNullOrEmpty();
        query["client_id"].ToString().ShouldBe("service-client");
        query["scope"].ToString().Split(' ').ShouldBe(["openid", "profile", "email"], ignoreOrder: true);
        query["redirect_uri"].ToString().ShouldEndWith(ExternalAuthSetup.ProviderCallbackPath);
        location.Query.ShouldNotContain("test-client-secret-value");
        location.Query.ShouldNotContain(Uri.EscapeDataString(ReturnUrl));
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("https://evil.example.test/auth/callback")]
    [InlineData("https://app.example.test/auth/callback/")]
    [InlineData("https://app.example.test/auth/callback?next=https://evil.example.test")]
    [InlineData("https://APP.example.test/auth/callback")]
    [InlineData("https://app.example.test/auth/callbackx")]
    [InlineData("//evil.example.test")]
    [InlineData("")]
    public async Task Challenge_ReturnUrlNotExactlyAllowlisted_Is400(string returnUrl)
    {
        await using var factory = new ExternalWebFactory(EnabledSettings());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var challenge = await client.GetAsync(new Uri($"{Prefix}/external/oidc/challenge?returnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative), Ct);

        challenge.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(challenge)).ShouldBe("invalid_return_url");
        challenge.Headers.Location.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Challenge_ProviderUnreachable_Is503AndLocalSignInIsUnaffected()
    {
        var settings = EnabledSettings();
        settings["Auth:External:Oidc:Authority"] = "https://127.0.0.1:1";
        await using var factory = new ExternalWebFactory(settings, staticProvider: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var challenge = await client.GetAsync(new Uri($"{Prefix}/external/oidc/challenge?returnUrl={Uri.EscapeDataString(ReturnUrl)}", UriKind.Relative), Ct);
        using var jwks = await client.GetAsync(new Uri("/.well-known/jwks.json", UriKind.Relative), Ct);

        challenge.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await AccountClient.ProblemCodeAsync(challenge)).ShouldBe("external_provider_unavailable");
        jwks.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Callback_WithoutExternalCookie_Is400()
    {
        await using var factory = new ExternalWebFactory(EnabledSettings());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var callback = await client.GetAsync(new Uri($"{Prefix}/external/oidc/callback", UriKind.Relative), Ct);

        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(callback)).ShouldBe("external_login_failed");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Callback_NoLinkAndUnknownEmail_ProvisionsUserWithVerifiedEmailLoginAndDefaultRole()
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;

        var result = await CallbackAsync(factory, Login("subject-1", "new@example.com", emailVerified: "true", name: "New Person"));

        result.Outcome.ShouldBe(ExternalCallbackOutcome.Provisioned);
        result.Code.ShouldNotBeNullOrEmpty();
        var userId = result.UserId!.Value;
        (await AuthTestServices.RowsAsync(factory.ConnectionString, $"SELECT email, email_confirmed, display_name, password_hash IS NULL FROM auth.users WHERE id = '{userId}'"))
            .ShouldBe(["new@example.com|True|New Person|True"]);
        (await AuthTestServices.RowsAsync(factory.ConnectionString, $"SELECT login_provider, provider_key FROM auth.user_logins WHERE user_id = '{userId}'"))
            .ShouldBe(["oidc|subject-1"]);
        (await AuthTestServices.RowsAsync(factory.ConnectionString, $"SELECT r.name FROM auth.user_roles ur JOIN auth.roles r ON r.id = ur.role_id WHERE ur.user_id = '{userId}'"))
            .ShouldBe(["Customer"]);
        (await AuthTestServices.ScalarAsync<long>(factory.ConnectionString, "SELECT count(*) FROM auth.outbox_messages WHERE type = 'auth.user-registered.v1'"))
            .ShouldBe(1);

        // Only the hash is stored.
        var stored = await AuthTestServices.RowsAsync(factory.ConnectionString, "SELECT encode(code_hash, 'base64'), length(code_hash) FROM auth.external_login_codes");
        stored.ShouldHaveSingleItem().ShouldEndWith("|32");
        stored[0].ShouldNotContain(result.Code!);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(null)]
    [InlineData("false")]
    public async Task Callback_EmailNotVerifiedByProvider_ProvisionsUnconfirmed_ExchangeIs403(string? emailVerified)
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;

        var result = await CallbackAsync(factory, Login("subject-2", "unverified@example.com", emailVerified));
        using var exchange = await ExchangeAsync(factory, result.Code!);

        result.Outcome.ShouldBe(ExternalCallbackOutcome.Provisioned);
        (await AuthTestServices.ScalarAsync<bool>(factory.ConnectionString, "SELECT email_confirmed FROM auth.users WHERE email = 'unverified@example.com'"))
            .ShouldBeFalse();
        exchange.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AccountClient.ProblemCodeAsync(exchange)).ShouldBe("email_not_verified");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Callback_LocalAccountWithTheEmail_IsAccountExistsAndNeverLinks()
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;
        var localId = await TestUsers.CreateAsync(factory.Services, "taken@example.com");

        var result = await CallbackAsync(factory, Login("attacker-subject", "TAKEN@example.com", emailVerified: "true"));

        result.Outcome.ShouldBe(ExternalCallbackOutcome.AccountExists);
        result.Code.ShouldBeNull();
        result.UserId.ShouldBeNull();
        (await AuthTestServices.ScalarAsync<long>(factory.ConnectionString, "SELECT count(*) FROM auth.user_logins")).ShouldBe(0);
        (await AuthTestServices.ScalarAsync<long>(factory.ConnectionString, "SELECT count(*) FROM auth.external_login_codes")).ShouldBe(0);
        (await AuthTestServices.ScalarAsync<long>(factory.ConnectionString, "SELECT count(*) FROM auth.users")).ShouldBe(1);
        (await AuthTestServices.ScalarAsync<Guid>(factory.ConnectionString, "SELECT id FROM auth.users")).ShouldBe(localId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Callback_ExistingLink_SignsInTheLinkedUserWhateverTheEmail()
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;
        var first = await CallbackAsync(factory, Login("subject-3", "linked@example.com", emailVerified: "true"));

        var second = await CallbackAsync(factory, Login("subject-3", "changed@example.com", emailVerified: "true"));

        second.Outcome.ShouldBe(ExternalCallbackOutcome.SignedIn);
        second.UserId.ShouldBe(first.UserId);
        second.Code.ShouldNotBe(first.Code);
        (await AuthTestServices.ScalarAsync<long>(factory.ConnectionString, "SELECT count(*) FROM auth.users")).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Callback_NoEmailClaim_FailsWithoutProvisioning()
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;

        await using var scope = factory.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<IUseCaseExecutor<ExternalCallbackCommand, ExternalCallbackResult>>();
        var result = await executor.ExecuteAsync(new ExternalCallbackCommand(Login("subject-4", null, "true")), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("external_login_failed");
        (await AuthTestServices.ScalarAsync<long>(factory.ConnectionString, "SELECT count(*) FROM auth.users")).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Exchange_ValidCode_ReturnsTokensWithAmrExt_AndIsSingleUse()
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;
        var result = await CallbackAsync(factory, Login("subject-5", "exchange@example.com", emailVerified: "true"));

        using var first = await ExchangeAsync(factory, result.Code!);
        using var replay = await ExchangeAsync(factory, result.Code!);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        var tokens = (await first.Content.ReadFromJsonAsync<TokenBody>(Ct))!;
        var jwt = new JsonWebToken(tokens.AccessToken);
        jwt.Claims.Where(c => c.Type == "amr").Select(c => c.Value).ShouldBe(["ext"]);
        jwt.Subject.ShouldBe(result.UserId.ToString());
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(replay)).ShouldBe("invalid_exchange_code");

        using var client = new AccountClient(factory);
        using var me = await client.MeAsync(tokens.AccessToken);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var refresh = await client.RefreshAsync(tokens.RefreshToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Exchange_CodeExpiresAfter60Seconds()
    {
        var (factory, clock) = await CreateHostAsync();
        await using var host = factory;
        var early = await CallbackAsync(factory, Login("subject-6", "early@example.com", emailVerified: "true"));
        var late = await CallbackAsync(factory, Login("subject-7", "late@example.com", emailVerified: "true"));

        clock.Advance(TimeSpan.FromSeconds(59));
        using var withinLifetime = await ExchangeAsync(factory, early.Code!);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var expired = await ExchangeAsync(factory, late.Code!);

        withinLifetime.StatusCode.ShouldBe(HttpStatusCode.OK);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(expired)).ShouldBe("invalid_exchange_code");
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("not-a-code")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Exchange_MalformedOrUnknownCode_Is401(string code)
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;

        using var exchange = await ExchangeAsync(factory, code);

        exchange.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(exchange)).ShouldBe("invalid_exchange_code");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Exchange_CodeIsNeverABearerToken()
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;
        var result = await CallbackAsync(factory, Login("subject-8", "bearer@example.com", emailVerified: "true"));
        using var client = new AccountClient(factory);

        using var me = await client.MeAsync(result.Code!);

        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Exchange_UserWithTwoFactor_ReturnsMfaRequiredWithoutTokens()
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;
        var result = await CallbackAsync(factory, Login("subject-9", "mfa@example.com", emailVerified: "true"));
        await AuthTestServices.ExecuteAsync(factory.ConnectionString, $"UPDATE auth.users SET two_factor_enabled = true WHERE id = '{result.UserId}'");

        using var exchange = await ExchangeAsync(factory, result.Code!);

        exchange.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        body.RootElement.TryGetProperty("accessToken", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Exchange_UserLockedAfterCallback_Is401()
    {
        var (factory, _) = await CreateHostAsync();
        await using var host = factory;
        var result = await CallbackAsync(factory, Login("subject-10", "locked@example.com", emailVerified: "true"));
        await AuthTestServices.ExecuteAsync(factory.ConnectionString, $"UPDATE auth.users SET lockout_end = 'infinity' WHERE id = '{result.UserId}'");

        using var exchange = await ExchangeAsync(factory, result.Code!);

        exchange.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestServices.ScalarAsync<long>(factory.ConnectionString, "SELECT count(*) FROM auth.sessions")).ShouldBe(0);
    }

    /// <summary>A fake provider identity, as <see cref="SignInManager{TUser}.GetExternalLoginInfoAsync"/> would return it.</summary>
    private static ExternalLoginInfo Login(string subject, string? email, string? emailVerified, string? name = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
        if (email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        if (emailVerified is not null)
        {
            claims.Add(new Claim(ExternalCallbackUseCase.EmailVerifiedClaim, emailVerified));
        }

        if (name is not null)
        {
            claims.Add(new Claim(ClaimTypes.Name, name));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ExternalOidcOptions.Scheme));
        return new ExternalLoginInfo(principal, ExternalOidcOptions.Scheme, subject, "Test provider");
    }

    private static async Task<ExternalCallbackResult> CallbackAsync(AccountWebFactory factory, ExternalLoginInfo login)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<IUseCaseExecutor<ExternalCallbackCommand, ExternalCallbackResult>>();
        var result = await executor.ExecuteAsync(new ExternalCallbackCommand(login), Ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Code : null);
        return result.Value;
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(AccountWebFactory factory, string code)
    {
        using var client = factory.CreateClient();
        return await client.PostAsJsonAsync(new Uri($"{Prefix}/external/exchange", UriKind.Relative), new { code }, Ct);
    }

    private static string AccessToken(ServiceWebFactory factory) =>
        factory.Services.GetRequiredService<AccessTokenIssuer>()
            .Issue(new AccessTokenSubject(Guid.CreateVersion7(), "probe@example.com", Guid.CreateVersion7(), [], [], ["pwd"]))
            .Token;

    private async Task<(AccountWebFactory Factory, ManualTimeProvider Clock)> CreateHostAsync() =>
        await MfaTestHost.CreateAsync(postgres, EnabledSettings());

    /// <summary>A host with the external login enabled whose OIDC handler has a static provider configuration (no discovery fetch).</summary>
    /// <remarks>
    /// The endpoints resolve <see cref="SignInManager{TUser}"/>, whose store needs a configured connection string; these
    /// tests never reach the database unless they pass <paramref name="connectionString"/>, so by default it points
    /// nowhere. With <paramref name="staticProvider"/> the OIDC handler gets a static provider configuration instead of
    /// fetching the discovery document.
    /// </remarks>
    private sealed class ExternalWebFactory(
        IReadOnlyDictionary<string, string?> overrides,
        bool staticProvider = true,
        string connectionString = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1") : ServiceWebFactory
    {
        protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides =>
            new Dictionary<string, string?>(overrides, StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = connectionString,
            };

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (!staticProvider)
            {
                return;
            }

            builder.ConfigureTestServices(services => services.PostConfigure<OpenIdConnectOptions>(ExternalOidcOptions.Scheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Authority,
                    AuthorizationEndpoint = $"{Authority}/authorize",
                    TokenEndpoint = $"{Authority}/token",
                };
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            }));
        }
    }
}
