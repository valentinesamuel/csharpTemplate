using System.Threading.RateLimiting;
using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Unit;

/// <summary>The P13 and P14 options (INV-17) and the Retry-After computation of the rate limiter.</summary>
public sealed class AccountOptionsTests
{
    private const string ValidConfirmUrl = "https://app.example.test/confirm-email";

    private const string ValidResetUrl = "https://app.example.test/reset-password";

    [Fact]
    public void RateLimitingOptions_NoConfiguration_UsePlanDefaults()
    {
        var options = Resolve<AuthRateLimitingOptions>([]);

        options.Credentials.PermitLimit.ShouldBe(10);
        options.Credentials.Window.ShouldBe(TimeSpan.FromMinutes(1));
        options.Email.PermitLimit.ShouldBe(5);
        options.Email.Window.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData("Auth:RateLimiting:Credentials:PermitLimit", "0", "Auth:RateLimiting:Credentials:PermitLimit must be between 1 and 100000.")]
    [InlineData("Auth:RateLimiting:Email:PermitLimit", "100001", "Auth:RateLimiting:Email:PermitLimit must be between 1 and 100000.")]
    [InlineData("Auth:RateLimiting:Email:Window", "00:00:00", "Auth:RateLimiting:Email:Window must be between 00:00:01 and 1.00:00:00.")]
    [InlineData("Auth:RateLimiting:Credentials:Window", "2.00:00:00", "Auth:RateLimiting:Credentials:Window must be between 00:00:01 and 1.00:00:00.")]
    public void RateLimitingOptions_InvalidValue_FailsNamingTheKey(string key, string value, string expected)
    {
        Should.Throw<OptionsValidationException>(() => Resolve<AuthRateLimitingOptions>(new() { [key] = value }))
            .Failures.ShouldContain(expected);
    }

    [Theory]
    [InlineData("https://app.example.com/confirm", true)]
    [InlineData("http://localhost:3000/confirm-email?lang=en", true)]
    [InlineData("/confirm-email", false)]
    [InlineData("ftp://app.example.com/confirm", false)]
    [InlineData("https://app.example.com/#/confirm", false)]
    [InlineData("", false)]
    public void LinksOptions_ConfirmEmailUrl_MustBeAbsoluteHttpWithoutFragment(string url, bool valid)
    {
        var settings = new Dictionary<string, string?> { ["Auth:Links:ConfirmEmailUrl"] = url, ["Auth:Links:ResetPasswordUrl"] = ValidResetUrl };
        if (valid)
        {
            Resolve<AuthLinksOptions>(settings).ConfirmEmailUrl.ShouldBe(url);
        }
        else
        {
            Should.Throw<OptionsValidationException>(() => Resolve<AuthLinksOptions>(settings))
                .Failures.ShouldBe(["Auth:Links:ConfirmEmailUrl is required: an absolute http or https URL without a fragment."]);
        }
    }

    [Theory]
    [InlineData("https://app.example.com/reset-password", true)]
    [InlineData("http://localhost:3000/reset?lang=en", true)]
    [InlineData("/reset-password", false)]
    [InlineData("https://app.example.com/#/reset", false)]
    [InlineData(null, false)]
    public void LinksOptions_ResetPasswordUrl_IsRequiredAbsoluteHttpWithoutFragment(string? url, bool valid)
    {
        var settings = new Dictionary<string, string?> { ["Auth:Links:ConfirmEmailUrl"] = ValidConfirmUrl, ["Auth:Links:ResetPasswordUrl"] = url };
        if (valid)
        {
            Resolve<AuthLinksOptions>(settings).ResetPasswordUrl.ShouldBe(url);
        }
        else
        {
            Should.Throw<OptionsValidationException>(() => Resolve<AuthLinksOptions>(settings))
                .Failures.ShouldBe(["Auth:Links:ResetPasswordUrl is required: an absolute http or https URL without a fragment."]);
        }
    }

    [Theory]
    [InlineData("Example Service", true)]
    [InlineData("service-name", true)]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("Example:Service", false)]
    [InlineData("Example\nService", false)]
    public void MfaOptions_Issuer_IsRequiredWithoutColonOrControlCharacters(string? issuer, bool valid)
    {
        var settings = new Dictionary<string, string?> { ["Auth:Mfa:Issuer"] = issuer };
        if (valid)
        {
            Resolve<AuthMfaOptions>(settings).Issuer.ShouldBe(issuer);
        }
        else
        {
            Should.Throw<OptionsValidationException>(() => Resolve<AuthMfaOptions>(settings))
                .Failures.ShouldBe(["Auth:Mfa:Issuer is required: 1 to 64 characters, without ':' or control characters."]);
        }
    }

    [Fact]
    public void AuthOptions_DefaultRole_DefaultsToCustomerAndRejectsUnknownRoles()
    {
        Resolve<AuthOptions>([]).DefaultRole.ShouldBe("Customer");
        Resolve<AuthOptions>(new() { ["Auth:DefaultRole"] = "Staff" }).DefaultRole.ShouldBe("Staff");
        Should.Throw<OptionsValidationException>(() => Resolve<AuthOptions>(new() { ["Auth:DefaultRole"] = "Root" }))
            .Failures.ShouldContain("Auth:DefaultRole must be one of the seed roles: Admin, Staff, Customer.");
    }

    [Fact]
    public void RetryAfterSeconds_SlidingWindowLimiterRejects_IsAtMostOneSegment()
    {
        var settings = new AuthRateLimitingOptions.WindowSettings { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) };
        using var limiter = new SlidingWindowRateLimiter(settings.ToLimiterOptions());
        using var granted = limiter.AttemptAcquire();
        using var rejected = limiter.AttemptAcquire();

        granted.IsAcquired.ShouldBeTrue();
        rejected.IsAcquired.ShouldBeFalse();
        AuthRateLimitingSetup.RetryAfterSeconds(rejected, settings).ShouldBeInRange(1, 10);
    }

    [Fact]
    public void RetryAfterSeconds_LeaseWithHint_RoundsTheHintUp()
    {
        var settings = new AuthRateLimitingOptions.WindowSettings();

        AuthRateLimitingSetup.RetryAfterSeconds(new HintLease(TimeSpan.FromMilliseconds(2100)), settings).ShouldBe(3);
        AuthRateLimitingSetup.RetryAfterSeconds(new HintLease(TimeSpan.FromMilliseconds(10)), settings).ShouldBe(1);
    }

    private static T Resolve<T>(Dictionary<string, string?> settings)
        where T : class
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        AuthRateLimitingOptions.Register(services, configuration);
        AuthLinksOptions.Register(services, configuration);
        AuthOptions.Register(services, configuration);
        AuthMfaOptions.Register(services, configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<T>>().Value;
    }

    private sealed class HintLease(TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = retryAfter;
            return metadataName == MetadataName.RetryAfter.Name;
        }
    }
}
