using System.Security.Claims;
using Acme.Service.Modules.Auth.External;
using Acme.Service.Modules.Auth.Features.ExternalCallback;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Unit;

/// <summary>The external login options (INV-17), the exact-match return URL rule, the one-time code format and the claim reading.</summary>
public sealed class ExternalLoginUnitTests
{
    private const string ReturnUrl = "https://app.example.test/auth/callback";

    [Fact]
    public void Options_Disabled_NeedNothingElse()
    {
        var options = Resolve([], allowHttpAuthority: false);

        options.Enabled.ShouldBeFalse();
        options.EffectiveScopes.ShouldBe(["openid", "profile", "email"]);
    }

    [Fact]
    public void Options_EnabledAndComplete_AreValid()
    {
        var options = Resolve(Enabled(), allowHttpAuthority: false);

        options.Enabled.ShouldBeTrue();
        options.AllowedReturnUrls.ShouldBe([ReturnUrl]);
    }

    [Theory]
    [InlineData("Auth:External:Oidc:Authority", null, "Auth:External:Oidc:Authority is required when Auth:External:Oidc:Enabled=true: an absolute https URL.")]
    [InlineData("Auth:External:Oidc:Authority", "http://idp.example.test", "Auth:External:Oidc:Authority is required when Auth:External:Oidc:Enabled=true: an absolute https URL.")]
    [InlineData("Auth:External:Oidc:ClientId", "", "Auth:External:Oidc:ClientId is required when Auth:External:Oidc:Enabled=true.")]
    [InlineData("Auth:External:Oidc:ClientSecret", null, "Auth:External:Oidc:ClientSecret is required when Auth:External:Oidc:Enabled=true.")]
    [InlineData("Auth:External:Oidc:AllowedReturnUrls:0", null, "Auth:External:Oidc:AllowedReturnUrls must list at least one URL when Auth:External:Oidc:Enabled=true.")]
    [InlineData("Auth:External:Oidc:AllowedReturnUrls:0", "/relative", "Auth:External:Oidc:AllowedReturnUrls entries must be absolute http or https URLs without a query or fragment.")]
    [InlineData("Auth:External:Oidc:AllowedReturnUrls:0", "https://app.example.test/cb?x=1", "Auth:External:Oidc:AllowedReturnUrls entries must be absolute http or https URLs without a query or fragment.")]
    [InlineData("Auth:External:Oidc:AllowedReturnUrls:0", "https://app.example.test/#/cb", "Auth:External:Oidc:AllowedReturnUrls entries must be absolute http or https URLs without a query or fragment.")]
    [InlineData("Auth:External:Oidc:Scopes:0", "profile", "Auth:External:Oidc:Scopes must contain 'openid' and only non-empty scope names without spaces.")]
    public void Options_EnabledWithMissingOrInvalidSetting_FailNamingTheKey(string key, string? value, string expected)
    {
        var settings = Enabled();
        if (value is null)
        {
            settings.Remove(key);
        }
        else
        {
            settings[key] = value;
        }

        Should.Throw<OptionsValidationException>(() => Resolve(settings, allowHttpAuthority: false))
            .Failures.ShouldBe([expected]);
    }

    [Fact]
    public void Options_HttpAuthority_IsAllowedOnlyInDevelopment()
    {
        var settings = Enabled();
        settings["Auth:External:Oidc:Authority"] = "http://localhost:8080/realms/dev";

        Resolve(settings, allowHttpAuthority: true).Authority.ShouldBe("http://localhost:8080/realms/dev");
        Should.Throw<OptionsValidationException>(() => Resolve(settings, allowHttpAuthority: false));
    }

    [Fact]
    public async Task Start_EnabledWithMissingConfiguration_ExitsWith1NamingTheKey()
    {
        var (exitCode, output) = await HostProcess.RunAsync(new Dictionary<string, string>
        {
            ["Auth__External__Oidc__Enabled"] = "true",
        });

        exitCode.ShouldBe(1);
        output.ShouldContain("Auth:External:Oidc:Authority is required when Auth:External:Oidc:Enabled=true");
    }

    [Fact]
    public async Task Start_EnabledWithoutReturnUrls_ExitsWith1AndNeverPrintsTheSecret()
    {
        var (exitCode, output) = await HostProcess.RunAsync(new Dictionary<string, string>
        {
            ["Auth__External__Oidc__Enabled"] = "true",
            ["Auth__External__Oidc__Authority"] = "https://idp.example.test",
            ["Auth__External__Oidc__ClientId"] = "service-client",
            ["Auth__External__Oidc__ClientSecret"] = "startup-secret-value-0123456789",
        });

        exitCode.ShouldBe(1);
        output.ShouldContain("Auth:External:Oidc:AllowedReturnUrls must list at least one URL");
        output.ShouldNotContain("startup-secret-value-0123456789");
    }

    [Theory]
    [InlineData(ReturnUrl, true)]
    [InlineData("https://app.example.test/auth/callback/", false)]
    [InlineData("https://APP.example.test/auth/callback", false)]
    [InlineData("https://app.example.test/auth/Callback", false)]
    [InlineData("https://app.example.test/auth/callback?x=1", false)]
    [InlineData("https://app.example.test/auth", false)]
    [InlineData("https://evil.example.test/auth/callback", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedReturnUrl_IsExactOrdinalMatch(string? returnUrl, bool allowed)
    {
        var options = new ExternalOidcOptions { AllowedReturnUrls = [ReturnUrl] };

        options.IsAllowedReturnUrl(returnUrl).ShouldBe(allowed);
    }

    [Fact]
    public void Code_NewCode_HashesToTheStoredHashAndIs32Bytes()
    {
        var (code, hash) = ExternalLoginCode.NewCode();
        var (other, _) = ExternalLoginCode.NewCode();

        code.Length.ShouldBe(43);
        code.ShouldNotBe(other);
        hash.Length.ShouldBe(32);
        ExternalLoginCode.TryHash(code, out var again).ShouldBeTrue();
        again.ShouldBe(hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("eyJhbGciOiJSUzI1NiIsInR5cCI6ImF0K2p3dCJ9.e30.sig")]
    public void Code_TryHash_RejectsMalformedValues(string? code)
    {
        ExternalLoginCode.TryHash(code, out var hash).ShouldBeFalse();
        hash.ShouldBeEmpty();
    }

    [Fact]
    public void Code_ExpiresSixtySecondsAfterIssue()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(20000);

        var code = new ExternalLoginCode(Guid.CreateVersion7(), new byte[32], now);

        code.ExpiresAt.ShouldBe(now.AddSeconds(60));
        code.ConsumedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData(null, false)]
    public void IsEmailVerified_OnlyAnExplicitTrue(string? value, bool expected)
    {
        var claims = value is null ? [] : new[] { new Claim("email_verified", value) };

        ExternalCallbackUseCase.IsEmailVerified(new ClaimsPrincipal(new ClaimsIdentity(claims))).ShouldBe(expected);
    }

    [Fact]
    public void EmailOf_ReadsMappedOrRawClaimAndTrims()
    {
        ExternalCallbackUseCase.EmailOf(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, " a@example.com ")]))).ShouldBe("a@example.com");
        ExternalCallbackUseCase.EmailOf(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "b@example.com")]))).ShouldBe("b@example.com");
        ExternalCallbackUseCase.EmailOf(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", " ")]))).ShouldBeNull();
    }

    [Fact]
    public void Result_ToString_NeverPrintsTheCode()
    {
        var result = new ExternalCallbackResult(ExternalCallbackOutcome.SignedIn, Guid.CreateVersion7(), "secret-code-value");

        result.ToString().ShouldNotContain("secret-code-value");
    }

    private static Dictionary<string, string?> Enabled() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Auth:External:Oidc:Enabled"] = "true",
        ["Auth:External:Oidc:Authority"] = "https://idp.example.test",
        ["Auth:External:Oidc:ClientId"] = "service-client",
        ["Auth:External:Oidc:ClientSecret"] = "unit-test-secret",
        ["Auth:External:Oidc:AllowedReturnUrls:0"] = ReturnUrl,
    };

    private static ExternalOidcOptions Resolve(Dictionary<string, string?> settings, bool allowHttpAuthority)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        ExternalOidcOptions.Register(services, configuration, allowHttpAuthority);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ExternalOidcOptions>>().Value;
    }
}
