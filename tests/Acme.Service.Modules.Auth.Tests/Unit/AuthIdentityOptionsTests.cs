using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Unit;

public sealed class AuthIdentityOptionsTests
{
    [Fact]
    public void IdentityOptions_Defaults_ApplyThePlanPolicy()
    {
        using var services = Build(new Dictionary<string, string?>());

        var identity = services.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var tokens = services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;

        identity.Password.RequiredLength.ShouldBe(12);
        identity.Password.RequireDigit.ShouldBeFalse();
        identity.Password.RequireUppercase.ShouldBeFalse();
        identity.Password.RequireLowercase.ShouldBeFalse();
        identity.Password.RequireNonAlphanumeric.ShouldBeFalse();
        identity.User.RequireUniqueEmail.ShouldBeTrue();
        identity.Lockout.AllowedForNewUsers.ShouldBeTrue();
        identity.Lockout.MaxFailedAccessAttempts.ShouldBe(5);
        identity.Lockout.DefaultLockoutTimeSpan.ShouldBe(TimeSpan.FromMinutes(15));
        tokens.TokenLifespan.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public void IdentityOptions_Configured_AreApplied()
    {
        using var services = Build(new Dictionary<string, string?>
        {
            ["Auth:Identity:Password:RequiredLength"] = "16",
            ["Auth:Identity:Lockout:MaxFailedAccessAttempts"] = "3",
            ["Auth:Identity:Lockout:DefaultLockoutTimeSpan"] = "00:30:00",
            ["Auth:Identity:TokenLifespan"] = "02:00:00",
        });

        var identity = services.GetRequiredService<IOptions<IdentityOptions>>().Value;

        identity.Password.RequiredLength.ShouldBe(16);
        identity.Lockout.MaxFailedAccessAttempts.ShouldBe(3);
        identity.Lockout.DefaultLockoutTimeSpan.ShouldBe(TimeSpan.FromMinutes(30));
        services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value.TokenLifespan.ShouldBe(TimeSpan.FromHours(2));
    }

    [Theory]
    [InlineData("Auth:Identity:Password:RequiredLength", "8", "Auth:Identity:Password:RequiredLength must be between 12 and 128.")]
    [InlineData("Auth:Identity:Lockout:MaxFailedAccessAttempts", "0", "Auth:Identity:Lockout:MaxFailedAccessAttempts must be at least 1.")]
    [InlineData("Auth:Identity:Lockout:DefaultLockoutTimeSpan", "00:00:00", "Auth:Identity:Lockout:DefaultLockoutTimeSpan must be positive.")]
    [InlineData("Auth:Identity:TokenLifespan", "-00:01:00", "Auth:Identity:TokenLifespan must be positive.")]
    public void Validate_InvalidValue_FailsNamingTheKey(string key, string value, string expected)
    {
        using var services = Build(new Dictionary<string, string?> { [key] = value });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<AuthIdentityOptions>>().Value);

        exception.Failures.ShouldContain(expected);
    }

    [Fact]
    public void HashRecoveryCode_IsLowerHexSha256()
    {
        // SHA-256("abc"), FIPS 180-2 test vector.
        AuthUserStore.HashRecoveryCode("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    private static ServiceProvider Build(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        AuthIdentityOptions.Register(services, configuration);
        return services.BuildServiceProvider();
    }
}
