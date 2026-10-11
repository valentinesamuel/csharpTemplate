using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Persistence;

[Collection(AuthDatabaseGroup.Name)]
public sealed class UserDirectoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetUserSummaryAsync_ReturnsSummaryNullForUnknownAndInactiveWhenLocked()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await using var factory = new AuthWebFactory(connectionString);
        var userId = await TestUsers.CreateAsync(factory.Services, "dir@example.com", "Dee Rectory");

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();

        (await directory.GetUserSummaryAsync(userId, Ct)).ShouldBe(new UserSummary(userId, "dir@example.com", "Dee Rectory", IsActive: true));
        (await directory.GetUserSummaryAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var user = (await userManager.FindByIdAsync(userId.ToString()))!;
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);

        (await directory.GetUserSummaryAsync(userId, Ct))!.IsActive.ShouldBeFalse();
    }
}
