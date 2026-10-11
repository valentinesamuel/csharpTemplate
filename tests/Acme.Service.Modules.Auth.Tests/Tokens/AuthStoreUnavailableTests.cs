using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.Modules.Auth.Sessions;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Tokens;

/// <summary>
/// Auth database unavailable (INV-13, D-29): stateless endpoints keep authenticating, live-session endpoints fail closed
/// with 503 <c>session_store_unavailable</c>, and refresh returns 503.
/// </summary>
public sealed class AuthStoreUnavailableTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Timeout=2";

    [Theory]
    [InlineData(UnreachableDatabase)]
    [InlineData(null)]
    public async Task GetLiveSessionEndpoint_AuthDbUnavailable_Returns503SessionStoreUnavailable(string? connectionString)
    {
        await using var factory = new ProbeWebFactory(connectionString);
        var token = ProbeClient.IssueAccessToken(factory);

        using var live = await ProbeClient.GetAsync(factory, ProbeModule.LiveSessionPath, token);

        live.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        live.Headers.RetryAfter?.Delta.ShouldBe(TimeSpan.FromSeconds(5));
        live.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await ProbeClient.ProblemCodeAsync(live)).ShouldBe("session_store_unavailable");
    }

    [Fact]
    public async Task GetStatelessEndpoints_AuthDbUnavailable_KeepAuthenticating()
    {
        await using var factory = new ProbeWebFactory(UnreachableDatabase);
        var token = ProbeClient.IssueAccessToken(factory, null, "users.read");

        using var authenticated = await ProbeClient.GetAsync(factory, ProbeModule.AuthenticatedPath, token);
        using var permission = await ProbeClient.GetAsync(factory, ProbeModule.PermissionPath, token);

        authenticated.StatusCode.ShouldBe(HttpStatusCode.OK);
        permission.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetLiveSessionEndpoint_NoToken_Returns401Challenge()
    {
        await using var factory = new ProbeWebFactory(UnreachableDatabase);

        using var response = await ProbeClient.GetAsync(factory, ProbeModule.LiveSessionPath);

        await TokenValidationTests.ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task RefreshAsync_AuthDbUnavailable_ReturnsUnavailable()
    {
        await using var factory = new ProbeWebFactory(UnreachableDatabase);
        var refreshToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenService.TokenBytes));

        var result = await ProbeClient.WithRefreshServiceAsync(factory, service => service.RefreshAsync(refreshToken, ClientInfo.None, TestContext.Current.CancellationToken));

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Unavailable);
        result.Error.Code.ShouldBe("database_unavailable");
    }
}
