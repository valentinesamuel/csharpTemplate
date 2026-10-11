using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Seeding;

/// <summary>The <c>migrate</c> verb creates the auth schema and seed data; the web app never does (INV-16).</summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class AuthMigrationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_FreshDatabase_CreatesAuthSchemaTablesAndSeedRoles()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);

        await AuthTestServices.MigrateAsync(connectionString);

        (await AuthTestServices.SchemaExistsAsync(connectionString, "auth")).ShouldBeTrue();
        (await AuthTestServices.RowsAsync(connectionString, "SELECT name FROM auth.roles ORDER BY 1"))
            .ShouldBe(["Admin", "Customer", "Staff"]);
        (await AuthTestServices.RowsAsync(
            connectionString,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'auth' ORDER BY 1"))
            .ShouldBe(
            [
                "__ef_migrations_history", "data_protection_keys", "external_login_codes", "inbox_messages", "mfa_challenges", "outbox_messages",
                "permissions", "refresh_tokens", "role_claims", "role_permissions", "roles", "sessions", "totp_time_steps",
                "user_claims", "user_logins", "user_roles", "user_tokens", "users",
            ]);
        var migrations = await AuthTestServices.RowsAsync(connectionString, "SELECT migration_id FROM auth.__ef_migrations_history ORDER BY 1");
        migrations.Count.ShouldBe(4);
        migrations[0].ShouldEndWith("_Initial");
        migrations[1].ShouldEndWith("_AddSessionsAndRefreshTokens");
        migrations[2].ShouldEndWith("_AddMfaChallenges");
        migrations[3].ShouldEndWith("_AddExternalLoginCodes");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_FreshDatabase_GrantsExactlyTheDefaultPermissions()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);

        await AuthTestServices.MigrateAsync(connectionString);

        // migrate seeds the permissions of every hosted module (§0.8).
        (await AuthTestServices.RowsAsync(connectionString, "SELECT name, module FROM auth.permissions ORDER BY 1"))
            .ShouldBe(
            [
                // <sample:orders>
                "orders.cancel|orders", "orders.create|orders", "orders.manage|orders", "orders.read|orders",
                // </sample:orders>
                "roles.read|auth", "users.manage|auth", "users.read|auth",
            ]);
        (await AuthTestServices.RowsAsync(connectionString, GrantsSql))
            .ShouldBe(
            [
                // <sample:orders>
                "Admin|orders.cancel", "Admin|orders.create", "Admin|orders.manage", "Admin|orders.read",
                // </sample:orders>
                "Admin|roles.read", "Admin|users.manage", "Admin|users.read",
                // <sample:orders>
                "Customer|orders.cancel", "Customer|orders.create", "Customer|orders.read",
                "Staff|orders.manage", "Staff|orders.read",
                // </sample:orders>
                "Staff|roles.read", "Staff|users.read",
            ]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WebApp_FreshDatabase_CreatesNothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await using (var factory = new AuthWebFactory(connectionString))
        {
            using var client = factory.CreateClient();
            using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);

            // Let the hosted services (outbox dispatcher, Data Protection key ring warm-up) run against the empty database.
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);

            ready.StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
        }

        (await AuthTestServices.SchemaExistsAsync(connectionString, "auth")).ShouldBeFalse();
        (await AuthTestServices.ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')"))
            .ShouldBe(0);
    }

    internal const string GrantsSql =
        "SELECT r.name, rp.permission_name FROM auth.role_permissions rp JOIN auth.roles r ON r.id = rp.role_id ORDER BY 1, 2";
}
