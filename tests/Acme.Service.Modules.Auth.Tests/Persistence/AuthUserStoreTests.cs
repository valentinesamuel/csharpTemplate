using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Persistence;

/// <summary>D-15 / INV-28: the authenticator key is encrypted at rest; recovery codes are hashed and redeem once.</summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class AuthUserStoreTests(PostgresFixture postgres)
{
    private const string TokenSql = "SELECT value FROM auth.user_tokens WHERE user_id = @user AND name = @name";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetAuthenticatorKey_StoresKeyEncrypted_AndReadsItBack()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await using var services = AuthTestServices.Create(connectionString);
        var userId = await TestUsers.CreateAsync(services, "totp@example.com");

        string key;
        await using (var scope = services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            var user = (await userManager.FindByIdAsync(userId.ToString()))!;
            (await userManager.ResetAuthenticatorKeyAsync(user)).Succeeded.ShouldBeTrue();
            key = (await userManager.GetAuthenticatorKeyAsync(user)).ShouldNotBeNull();
        }

        var raw = await AuthTestServices.ScalarAsync<string>(connectionString, TokenSql, ("user", userId), ("name", "AuthenticatorKey"));

        key.Length.ShouldBeGreaterThan(0);
        raw.ShouldNotBeNullOrEmpty();
        raw.ShouldNotBe(key);
        raw.ShouldNotContain(key);

        // A fresh provider over the same database (another replica) decrypts it with the shared key ring.
        await using var other = AuthTestServices.Create(connectionString);
        await using var otherScope = other.CreateAsyncScope();
        var otherManager = otherScope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var sameUser = (await otherManager.FindByIdAsync(userId.ToString()))!;
        (await otherManager.GetAuthenticatorKeyAsync(sameUser)).ShouldBe(key);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RecoveryCodes_StoredAsSha256Hashes_RedeemOnce()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await using var services = AuthTestServices.Create(connectionString);
        var userId = await TestUsers.CreateAsync(services, "codes@example.com");

        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var user = (await userManager.FindByIdAsync(userId.ToString()))!;
        var codes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10)).ShouldNotBeNull().ToList();

        var raw = (await AuthTestServices.ScalarAsync<string>(connectionString, TokenSql, ("user", userId), ("name", "RecoveryCodes"))).ShouldNotBeNull();
        codes.Count.ShouldBe(10);
        foreach (var code in codes)
        {
            raw.ShouldNotContain(code);
        }

        raw.Split(';').ShouldBe(codes.Select(AuthUserStore.HashRecoveryCode), ignoreOrder: true);
        raw.Split(';').ShouldAllBe(h => h.Length == 64);

        (await userManager.CountRecoveryCodesAsync(user)).ShouldBe(10);
        (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, codes[3])).Succeeded.ShouldBeTrue();
        (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, codes[3])).Succeeded.ShouldBeFalse();
        (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, "not-a-code")).Succeeded.ShouldBeFalse();
        (await userManager.CountRecoveryCodesAsync(user)).ShouldBe(9);

        // The codes left after a redemption still redeem (they are not hashed a second time).
        (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, codes[0])).Succeeded.ShouldBeTrue();
        (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, codes[9])).Succeeded.ShouldBeTrue();
        (await userManager.CountRecoveryCodesAsync(user)).ShouldBe(7);
        (await AuthTestServices.ScalarAsync<string>(connectionString, TokenSql, ("user", userId), ("name", "RecoveryCodes"))).ShouldNotBeNull()
            .Split(';').ShouldBe(codes.Where((_, i) => i is not (0 or 3 or 9)).Select(AuthUserStore.HashRecoveryCode), ignoreOrder: true);
    }
}
