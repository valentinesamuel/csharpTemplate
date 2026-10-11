using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Admin;

/// <summary>
/// The RBAC admin endpoints (D-22, §0.8): permission and live-session protected, role changes reach tokens at the next
/// refresh, the last administrator keeps Admin, and a lock ends every session.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class AdminEndpointsTests(PostgresFixture postgres)
{
    private const string Prefix = "/api/v1/auth";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListRoles_CustomerIs403_StaffGetsRolesWithPermissions()
    {
        await using var factory = await CreateHostAsync();
        var connectionString = factory.ConnectionString;
        using var client = new AccountClient(factory);
        await client.RegisterConfirmedAsync("customer@example.com");
        await client.RegisterConfirmedAsync("staff@example.com");
        await GrantAsync(connectionString, "staff@example.com", "Staff");
        var customer = await client.LoginTokensAsync("customer@example.com");
        var staff = await client.LoginTokensAsync("staff@example.com");

        using var asCustomer = await SendAsync(factory, HttpMethod.Get, "/roles", customer.AccessToken);
        using var asStaff = await SendAsync(factory, HttpMethod.Get, "/roles", staff.AccessToken);
        using var anonymous = await SendAsync(factory, HttpMethod.Get, "/roles", null);

        asCustomer.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        asStaff.StatusCode.ShouldBe(HttpStatusCode.OK, await asStaff.Content.ReadAsStringAsync(Ct));
        var roles = (await asStaff.Content.ReadFromJsonAsync<List<RoleBody>>(Ct))!;
        roles.Select(r => r.Name).ShouldBe(["Admin", "Customer", "Staff"]);
        var staffPermissions = roles.Single(r => r.Name == "Staff").Permissions;
        staffPermissions.ShouldContain("roles.read");
        staffPermissions.ShouldContain("users.read");
        staffPermissions.ShouldNotContain("users.manage");
        staffPermissions.ShouldBe([.. staffPermissions.Order(StringComparer.Ordinal)]);
        roles.Single(r => r.Name == "Admin").Permissions.ShouldContain("users.manage");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AdminEndpoints_RevokedSession_Are401SessionRevoked()
    {
        await using var factory = await CreateHostAsync();
        var connectionString = factory.ConnectionString;
        using var client = new AccountClient(factory);
        await client.RegisterConfirmedAsync("staff@example.com");
        await GrantAsync(connectionString, "staff@example.com", "Staff");
        var staff = await client.LoginTokensAsync("staff@example.com");

        using (var logoutAll = await client.LogoutAllAsync(staff.AccessToken))
        {
            logoutAll.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var roles = await SendAsync(factory, HttpMethod.Get, "/roles", staff.AccessToken);

        roles.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(roles)).ShouldBe("session_revoked");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetUser_StaffReadsAccount_UnknownIs404()
    {
        await using var factory = await CreateHostAsync();
        var connectionString = factory.ConnectionString;
        using var client = new AccountClient(factory);
        var customerId = await client.RegisterConfirmedAsync("customer@example.com");
        await client.RegisterConfirmedAsync("staff@example.com");
        await GrantAsync(connectionString, "staff@example.com", "Staff");
        var staff = await client.LoginTokensAsync("staff@example.com");

        using var found = await SendAsync(factory, HttpMethod.Get, $"/users/{customerId}", staff.AccessToken);
        using var missing = await SendAsync(factory, HttpMethod.Get, $"/users/{Guid.CreateVersion7()}", staff.AccessToken);
        using var assign = await SendAsync(factory, HttpMethod.Post, $"/users/{customerId}/roles/Staff", staff.AccessToken);

        found.StatusCode.ShouldBe(HttpStatusCode.OK, await found.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("id").GetGuid().ShouldBe(customerId);
        body.RootElement.GetProperty("email").GetString().ShouldBe("customer@example.com");
        body.RootElement.GetProperty("lockedOut").GetBoolean().ShouldBeFalse();
        body.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["Customer"]);
        body.RootElement.TryGetProperty("passwordHash", out _).ShouldBeFalse();
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AccountClient.ProblemCodeAsync(missing)).ShouldBe("user_not_found");

        // Staff can read but not manage (§0.8).
        assign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AssignAndUnassignRole_TakeEffectAtTheNextRefresh()
    {
        await using var factory = await CreateHostAsync();
        var connectionString = factory.ConnectionString;
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("user@example.com");
        await client.RegisterConfirmedAsync("admin@example.com");
        await GrantAsync(connectionString, "admin@example.com", "Admin");
        var admin = await client.LoginTokensAsync("admin@example.com");
        var user = await client.LoginTokensAsync("user@example.com");
        Permissions(user.AccessToken).ShouldNotContain("roles.read");

        using (var assign = await SendAsync(factory, HttpMethod.Post, $"/users/{userId}/roles/Staff", admin.AccessToken))
        {
            assign.StatusCode.ShouldBe(HttpStatusCode.NoContent, await assign.Content.ReadAsStringAsync(Ct));
        }

        using (var again = await SendAsync(factory, HttpMethod.Post, $"/users/{userId}/roles/Staff", admin.AccessToken))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The token issued before the change still carries the old permissions.
        using (var before = await SendAsync(factory, HttpMethod.Get, "/roles", user.AccessToken))
        {
            before.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var refreshed = await RefreshAsync(client, user.RefreshToken);
        Permissions(refreshed.AccessToken).ShouldContain("roles.read");
        Permissions(refreshed.AccessToken).ShouldContain("users.read");
        new JsonWebToken(refreshed.AccessToken).Claims.Where(c => c.Type == "role").Select(c => c.Value).ShouldBe(["Customer", "Staff"]);
        using (var after = await SendAsync(factory, HttpMethod.Get, "/roles", refreshed.AccessToken))
        {
            after.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var unassign = await SendAsync(factory, HttpMethod.Delete, $"/users/{userId}/roles/Staff", admin.AccessToken))
        {
            unassign.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var reduced = await RefreshAsync(client, refreshed.RefreshToken);
        Permissions(reduced.AccessToken).ShouldNotContain("roles.read");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AssignRole_UnknownRoleOrUser_Is404()
    {
        await using var factory = await CreateHostAsync();
        var connectionString = factory.ConnectionString;
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("user@example.com");
        await client.RegisterConfirmedAsync("admin@example.com");
        await GrantAsync(connectionString, "admin@example.com", "Admin");
        var admin = await client.LoginTokensAsync("admin@example.com");

        using var unknownRole = await SendAsync(factory, HttpMethod.Post, $"/users/{userId}/roles/Superuser", admin.AccessToken);
        using var unknownUser = await SendAsync(factory, HttpMethod.Post, $"/users/{Guid.CreateVersion7()}/roles/Staff", admin.AccessToken);

        unknownRole.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AccountClient.ProblemCodeAsync(unknownRole)).ShouldBe("role_not_found");
        unknownUser.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AccountClient.ProblemCodeAsync(unknownUser)).ShouldBe("user_not_found");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnassignAdmin_FromTheLastAdmin_Is409LastAdmin()
    {
        await using var factory = await CreateHostAsync();
        var connectionString = factory.ConnectionString;
        using var client = new AccountClient(factory);
        var firstId = await client.RegisterConfirmedAsync("first@example.com");
        var secondId = await client.RegisterConfirmedAsync("second@example.com");
        await GrantAsync(connectionString, "first@example.com", "Admin");
        var first = await client.LoginTokensAsync("first@example.com");

        using (var self = await SendAsync(factory, HttpMethod.Delete, $"/users/{firstId}/roles/Admin", first.AccessToken))
        {
            self.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await AccountClient.ProblemCodeAsync(self)).ShouldBe("last_admin");
        }

        // With a second administrator the first one can step down; then the second is the last one.
        using (var grant = await SendAsync(factory, HttpMethod.Post, $"/users/{secondId}/roles/Admin", first.AccessToken))
        {
            grant.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var stepDown = await SendAsync(factory, HttpMethod.Delete, $"/users/{firstId}/roles/Admin", first.AccessToken))
        {
            stepDown.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var second = await client.LoginTokensAsync("second@example.com");
        using (var last = await SendAsync(factory, HttpMethod.Delete, $"/users/{secondId}/roles/Admin", second.AccessToken))
        {
            last.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await AccountClient.ProblemCodeAsync(last)).ShouldBe("last_admin");
        }

        (await AuthTestServices.ScalarAsync<long>(
                connectionString,
                "SELECT count(*) FROM auth.user_roles ur JOIN auth.roles r ON r.id = ur.role_id WHERE r.name = 'Admin'"))
            .ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnassignAdmin_WhenTheOtherAdminIsLocked_Is409LastAdmin()
    {
        await using var factory = await CreateHostAsync();
        var connectionString = factory.ConnectionString;
        using var client = new AccountClient(factory);
        var firstId = await client.RegisterConfirmedAsync("first@example.com");
        var secondId = await client.RegisterConfirmedAsync("second@example.com");
        await GrantAsync(connectionString, "first@example.com", "Admin");
        await GrantAsync(connectionString, "second@example.com", "Admin");
        var first = await client.LoginTokensAsync("first@example.com");

        using (var lockSecond = await SendAsync(factory, HttpMethod.Post, $"/users/{secondId}/lock", first.AccessToken))
        {
            lockSecond.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var stepDown = await SendAsync(factory, HttpMethod.Delete, $"/users/{firstId}/roles/Admin", first.AccessToken);

        stepDown.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AccountClient.ProblemCodeAsync(stepDown)).ShouldBe("last_admin");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LockUser_RevokesEverySession_RefreshAndLoginAre401_UnlockRestoresLogin()
    {
        await using var factory = await CreateHostAsync();
        var connectionString = factory.ConnectionString;
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("user@example.com");
        await client.RegisterConfirmedAsync("admin@example.com");
        await GrantAsync(connectionString, "admin@example.com", "Admin");
        var admin = await client.LoginTokensAsync("admin@example.com");
        var user = await client.LoginTokensAsync("user@example.com");
        var stampBefore = await AuthTestServices.ScalarAsync<string>(
            connectionString, "SELECT security_stamp FROM auth.users WHERE id = @id", ("id", userId));

        using (var lockUser = await SendAsync(factory, HttpMethod.Post, $"/users/{userId}/lock", admin.AccessToken))
        {
            lockUser.StatusCode.ShouldBe(HttpStatusCode.NoContent, await lockUser.Content.ReadAsStringAsync(Ct));
        }

        using (var refresh = await client.RefreshAsync(user.RefreshToken))
        {
            refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AccountClient.ProblemCodeAsync(refresh)).ShouldBe("invalid_refresh_token");
        }

        using (var liveSession = await client.LogoutAllAsync(user.AccessToken))
        {
            liveSession.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AccountClient.ProblemCodeAsync(liveSession)).ShouldBe("session_revoked");
        }

        using (var login = await client.LoginAsync("user@example.com"))
        {
            login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await AuthTestServices.RowsAsync(connectionString, $"SELECT revoked_reason FROM auth.sessions WHERE user_id = '{userId}'"))
            .ShouldBe(["user_locked"]);
        (await AuthTestServices.ScalarAsync<string>(
            connectionString, "SELECT security_stamp FROM auth.users WHERE id = @id", ("id", userId))).ShouldNotBe(stampBefore);
        using (var read = await SendAsync(factory, HttpMethod.Get, $"/users/{userId}", admin.AccessToken))
        {
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
            body.RootElement.GetProperty("lockedOut").GetBoolean().ShouldBeTrue();
        }

        using (var unlock = await SendAsync(factory, HttpMethod.Post, $"/users/{userId}/unlock", admin.AccessToken))
        {
            unlock.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await client.LoginTokensAsync("user@example.com")).AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LockUser_WithoutUsersManage_Is403()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        var victimId = await client.RegisterConfirmedAsync("victim@example.com");
        await client.RegisterConfirmedAsync("customer@example.com");
        var customer = await client.LoginTokensAsync("customer@example.com");

        using var lockUser = await SendAsync(factory, HttpMethod.Post, $"/users/{victimId}/lock", customer.AccessToken);
        using var unlock = await SendAsync(factory, HttpMethod.Post, $"/users/{victimId}/unlock", customer.AccessToken);
        using var unassign = await SendAsync(factory, HttpMethod.Delete, $"/users/{victimId}/roles/Customer", customer.AccessToken);

        lockUser.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        unlock.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        unassign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.LoginTokensAsync("victim@example.com")).AccessToken.ShouldNotBeNullOrEmpty();
    }

    private static List<string> Permissions(string accessToken) =>
        [.. new JsonWebToken(accessToken).Claims.Where(c => c.Type == "perm").Select(c => c.Value)];

    private static async Task<TokenBody> RefreshAsync(AccountClient client, string refreshToken)
    {
        using var response = await client.RefreshAsync(refreshToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TokenBody>(Ct))!;
    }

    private static async Task<HttpResponseMessage> SendAsync(AccountWebFactory factory, HttpMethod method, string path, string? accessToken)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(Prefix + path, UriKind.Relative));
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task GrantAsync(string connectionString, string email, string role)
    {
        await using var services = AuthTestServices.Create(connectionString);
        (await AuthModule.GrantRoleAsync(services, email, role, Ct)).ShouldBe(0);
    }

    private async Task<AccountWebFactory> CreateHostAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        return new AccountWebFactory(connectionString);
    }

    private sealed record RoleBody(string Name, IReadOnlyList<string> Permissions);
}
