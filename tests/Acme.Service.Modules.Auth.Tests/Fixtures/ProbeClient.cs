using System.Net.Http.Headers;
using System.Text.Json;
using Acme.Service.Modules.Auth.Sessions;
using Acme.Service.Modules.Auth.Tokens;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>HTTP and token helpers over a <see cref="ProbeWebFactory"/>.</summary>
internal static class ProbeClient
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>GET <paramref name="path"/> with an optional bearer token.</summary>
    public static async Task<HttpResponseMessage> GetAsync(ProbeWebFactory factory, string path, string? bearer = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The <c>code</c> extension of a problem response.</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>An access token issued by the host's own issuer (Active key, host clock).</summary>
    public static string IssueAccessToken(ProbeWebFactory factory, Guid? sessionId = null, params string[] permissions) =>
        factory.Services.GetRequiredService<AccessTokenIssuer>()
            .Issue(new AccessTokenSubject(Guid.CreateVersion7(), "probe@example.com", sessionId ?? Guid.CreateVersion7(), [], permissions, ["pwd"]))
            .Token;

    /// <summary>Runs <paramref name="action"/> with a scoped <see cref="RefreshTokenService"/>.</summary>
    public static async Task<T> WithRefreshServiceAsync<T>(ProbeWebFactory factory, Func<RefreshTokenService, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<RefreshTokenService>());
    }
}
