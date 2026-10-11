using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Acme.Service.BuildingBlocks.Security;
using Acme.Service.Host.Modules;
using Acme.Service.Modules.Auth.Seeding;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Seeding;

[Collection(AuthDatabaseGroup.Name)]
public sealed class AuthSeederTests(PostgresFixture postgres)
{
    private const string SnapshotSql =
        "SELECT 'p', name, description, module FROM auth.permissions " +
        "UNION ALL SELECT 'r', id::text, name, normalized_name || ':' || concurrency_stamp FROM auth.roles " +
        "UNION ALL SELECT 'g', role_id::text, permission_name, '' FROM auth.role_permissions ORDER BY 1, 2, 3";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_RunTwice_LeavesTheSameState()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);

        await AuthTestServices.MigrateAsync(connectionString);
        var first = await AuthTestServices.RowsAsync(connectionString, SnapshotSql);
        await AuthTestServices.MigrateAsync(connectionString);
        var second = await AuthTestServices.RowsAsync(connectionString, SnapshotSql);

        first.ShouldNotBeEmpty();
        second.ShouldBe(first);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_PermissionNoLongerDeclared_DeletesItAndItsGrants()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        var widgets = new PermissionDefinition("widgets.read", "Read widgets.", ["Customer", "Admin"]);

        await RunSeederAsync(connectionString, new AuthModule(), new TestModule(widgets));
        (await AuthTestServices.RowsAsync(connectionString, AuthMigrationTests.GrantsSql)).ShouldContain("Customer|widgets.read");

        await RunSeederAsync(connectionString, new AuthModule());

        (await AuthTestServices.ScalarAsync<long>(connectionString, "SELECT count(*) FROM auth.permissions WHERE name = 'widgets.read'")).ShouldBe(0);
        (await AuthTestServices.RowsAsync(connectionString, AuthMigrationTests.GrantsSql))
            .ShouldBe(["Admin|roles.read", "Admin|users.manage", "Admin|users.read", "Staff|roles.read", "Staff|users.read"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_StrayRowsAndDrift_RestoresExactlyTheDeclaredDefaults()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await AuthTestServices.ExecuteAsync(connectionString, """
            INSERT INTO auth.permissions (name, description, module) VALUES ('legacy.read', 'Old.', 'legacy');
            INSERT INTO auth.role_permissions (role_id, permission_name) SELECT id, 'legacy.read' FROM auth.roles;
            DELETE FROM auth.role_permissions rp USING auth.roles r WHERE r.id = rp.role_id AND r.name = 'Admin' AND rp.permission_name = 'users.manage';
            INSERT INTO auth.role_permissions (role_id, permission_name) SELECT id, 'users.manage' FROM auth.roles WHERE name = 'Customer';
            UPDATE auth.permissions SET description = 'tampered' WHERE name = 'users.read';
            DELETE FROM auth.roles WHERE name = 'Staff';
            """);

        await RunSeederAsync(connectionString, new AuthModule());

        (await AuthTestServices.RowsAsync(connectionString, "SELECT name, description FROM auth.permissions ORDER BY 1"))
            .ShouldBe(["roles.read|Read roles.", "users.manage|Manage users: assign and unassign roles, lock and unlock accounts.", "users.read|Read users."]);
        (await AuthTestServices.RowsAsync(connectionString, "SELECT name FROM auth.roles ORDER BY 1")).ShouldBe(["Admin", "Customer", "Staff"]);
        (await AuthTestServices.RowsAsync(connectionString, AuthMigrationTests.GrantsSql))
            .ShouldBe(["Admin|roles.read", "Admin|users.manage", "Admin|users.read", "Staff|roles.read", "Staff|users.read"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_NonSeedRole_KeepsItsGrantsOfDeclaredPermissions()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await AuthTestServices.ExecuteAsync(connectionString, """
            INSERT INTO auth.roles (id, name, normalized_name, concurrency_stamp) VALUES ('0199c4a0-0000-7000-8000-000000000001', 'Auditor', 'AUDITOR', 'x');
            INSERT INTO auth.role_permissions (role_id, permission_name) VALUES ('0199c4a0-0000-7000-8000-000000000001', 'users.read');
            """);

        await RunSeederAsync(connectionString, new AuthModule());

        (await AuthTestServices.RowsAsync(connectionString, AuthMigrationTests.GrantsSql)).ShouldContain("Auditor|users.read");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_ConcurrentRunsOnEmptyCatalog_SerializeAndSucceed()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        var expected = await AuthTestServices.RowsAsync(connectionString, AuthMigrationTests.GrantsSql);
        await AuthTestServices.ExecuteAsync(connectionString, "DELETE FROM auth.roles; DELETE FROM auth.permissions;");

        // Each run has its own provider (own connections), like separate migrate jobs; no migration-runner lock here. The
        // runs declare the hosted modules, as migrate does, so the catalog matches the one migrate produced.
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => RunSeederAsync(connectionString, HostModules.Create()), Ct)));

        (await AuthTestServices.RowsAsync(connectionString, "SELECT name FROM auth.roles ORDER BY 1")).ShouldBe(["Admin", "Customer", "Staff"]);
        (await AuthTestServices.RowsAsync(connectionString, AuthMigrationTests.GrantsSql)).ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_ConcurrentRuns_AllSucceed()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => AuthTestServices.MigrateAsync(connectionString), Ct)));

        (await AuthTestServices.RowsAsync(connectionString, "SELECT name FROM auth.roles ORDER BY 1")).ShouldBe(["Admin", "Customer", "Staff"]);
    }

    private static async Task RunSeederAsync(string connectionString, params Acme.Service.BuildingBlocks.Web.Modules.IModule[] modules)
    {
        await using var services = AuthTestServices.Create(connectionString, modules);
        await using var scope = services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetServices<IPostMigrationStep>().OfType<AuthSeeder>().ShouldHaveSingleItem();
        seeder.Order.ShouldBeGreaterThan(100);
        await seeder.ExecuteAsync(Ct);
    }
}
