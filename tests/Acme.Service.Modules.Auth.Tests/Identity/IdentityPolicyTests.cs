using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Identity;

/// <summary>Password policy (NIST 800-63B), unique emails and lockout through the real Identity stack (WH-12).</summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class IdentityPolicyTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateAsync_PasswordPolicy_RequiresTwelveCharactersAndNoComposition()
    {
        await using var services = await CreateServicesAsync();
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();

        var tooShort = await userManager.CreateAsync(NewUser("short@example.com"), "elevenchars");
        var plainLowercase = await userManager.CreateAsync(NewUser("plain@example.com"), "twelvecharsx");

        tooShort.Succeeded.ShouldBeFalse();
        tooShort.Errors.Select(e => e.Code).ShouldBe(["PasswordTooShort"]);
        plainLowercase.Succeeded.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateAsync_DuplicateEmail_Fails()
    {
        await using var services = await CreateServicesAsync();
        await TestUsers.CreateAsync(services, "dup@example.com");
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();

        var user = NewUser("dup@example.com");
        user.UserName = "another-user-name";
        var result = await userManager.CreateAsync(user, TestUsers.Password);

        result.Succeeded.ShouldBeFalse();
        result.Errors.Select(e => e.Code).ShouldContain("DuplicateEmail");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckPasswordSignInAsync_FiveFailures_LocksOutFifteenMinutesWithoutCookieScheme()
    {
        await using var services = await CreateServicesAsync();
        var userId = await TestUsers.CreateAsync(services, "lock@example.com");
        await using var scope = services.CreateAsyncScope();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<AuthUser>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var user = (await userManager.FindByIdAsync(userId.ToString()))!;

        user.LockoutEnabled.ShouldBeTrue();
        for (var i = 0; i < 4; i++)
        {
            (await signInManager.CheckPasswordSignInAsync(user, "wrong password!", lockoutOnFailure: true)).Succeeded.ShouldBeFalse();
        }

        var fifth = await signInManager.CheckPasswordSignInAsync(user, "wrong password!", lockoutOnFailure: true);
        var correctWhileLocked = await signInManager.CheckPasswordSignInAsync(user, TestUsers.Password, lockoutOnFailure: true);

        fifth.IsLockedOut.ShouldBeTrue();
        correctWhileLocked.IsLockedOut.ShouldBeTrue();
        var lockoutEnd = (await userManager.GetLockoutEndDateAsync(user)).ShouldNotBeNull();
        (lockoutEnd - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
    }

    private async Task<ServiceProvider> CreateServicesAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        return AuthTestServices.Create(connectionString);
    }

    private static AuthUser NewUser(string email) => new() { UserName = email, Email = email, CreatedAt = DateTimeOffset.UtcNow };
}
