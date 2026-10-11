using Acme.Service.Host.Commands;
using Acme.Service.Host.Modules;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Cli;

/// <summary>The real <c>auth:grant-role</c> verb against a migrated database.</summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class GrantRoleTests(PostgresFixture postgres)
{
    private const string UserRolesSql =
        "SELECT u.email, r.name FROM auth.user_roles ur JOIN auth.users u ON u.id = ur.user_id JOIN auth.roles r ON r.id = ur.role_id ORDER BY 1, 2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_ExistingUserAndRole_GrantsIdempotently()
    {
        var connectionString = await MigratedDatabaseAsync();
        await using (var services = AuthTestServices.Create(connectionString))
        {
            await TestUsers.CreateAsync(services, "admin@example.com");
        }

        var first = await RunAsync(connectionString, "--email", "admin@example.com", "--role", "Admin");
        var second = await RunAsync(connectionString, "--email", "ADMIN@example.com", "--role", "admin");

        first.ShouldBe(0);
        second.ShouldBe(0);
        (await AuthTestServices.RowsAsync(connectionString, UserRolesSql)).ShouldBe(["admin@example.com|Admin"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_MissingUser_ReturnsNonZeroAndChangesNothing()
    {
        var connectionString = await MigratedDatabaseAsync();

        var exitCode = await RunAsync(connectionString, "--email", "nobody@x.io", "--role", "Admin");

        exitCode.ShouldNotBe(0);
        (await AuthTestServices.RowsAsync(connectionString, UserRolesSql)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_MissingRole_ReturnsNonZeroAndCreatesNoRole()
    {
        var connectionString = await MigratedDatabaseAsync();
        await using (var services = AuthTestServices.Create(connectionString))
        {
            await TestUsers.CreateAsync(services, "someone@example.com");
        }

        var exitCode = await RunAsync(connectionString, "--email", "someone@example.com", "--role", "Superuser");

        exitCode.ShouldNotBe(0);
        (await AuthTestServices.RowsAsync(connectionString, UserRolesSql)).ShouldBeEmpty();
        (await AuthTestServices.RowsAsync(connectionString, "SELECT name FROM auth.roles ORDER BY 1")).ShouldBe(["Admin", "Customer", "Staff"]);
    }

    private async Task<string> MigratedDatabaseAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        return connectionString;
    }

    private static Task<int> RunAsync(string connectionString, params string[] arguments) =>
        GrantRoleVerb.RunAsync(
            [GrantRoleVerb.Verb, .. arguments, $"--ConnectionStrings:Postgres={connectionString}"],
            HostModules.Create(),
            Ct);
}
