using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.DataProtection;

/// <summary>
/// D-27: two web hosts (replicas) over one database share the Data Protection key ring stored in
/// <c>auth.data_protection_keys</c>, so a token protected on A unprotects on B.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class SharedKeyRingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Protect_OnHostA_UnprotectsOnHostB()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await using var hostA = new AuthWebFactory(connectionString);
        await using var hostB = new AuthWebFactory(connectionString);
        _ = hostA.Services;
        _ = hostB.Services;

        var protectedOnA = hostA.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("tests.shared").Protect("payload");
        var unprotectedOnB = hostB.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("tests.shared").Unprotect(protectedOnA);

        unprotectedOnB.ShouldBe("payload");
        (await AuthTestServices.ScalarAsync<long>(connectionString, "SELECT count(*) FROM auth.data_protection_keys")).ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EmailConfirmationToken_IssuedOnHostA_ValidatesOnHostB()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await using var hostA = new AuthWebFactory(connectionString);
        await using var hostB = new AuthWebFactory(connectionString);
        var userId = await TestUsers.CreateAsync(hostA.Services, "replica@example.com");

        string token;
        await using (var scope = hostA.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            token = await manager.GenerateEmailConfirmationTokenAsync((await manager.FindByIdAsync(userId.ToString()))!);
        }

        await using (var scope = hostB.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            var user = (await manager.FindByIdAsync(userId.ToString()))!;
            (await manager.ConfirmEmailAsync(user, token)).Succeeded.ShouldBeTrue();
            user.EmailConfirmed.ShouldBeTrue();
        }
    }
}
