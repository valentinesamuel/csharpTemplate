using System.Net;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Tokens;

/// <summary>Token lifetime against the host's <see cref="TimeProvider"/> (15 min lifetime, 30 s clock skew).</summary>
public sealed class TokenLifetimeTests
{
    [Fact]
    public async Task Get_TokenExpired_Returns401()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new ProbeWebFactory(timeProvider: clock);
        var token = ProbeClient.IssueAccessToken(factory);

        using var fresh = await ProbeClient.GetAsync(factory, ProbeModule.AuthenticatedPath, token);
        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(20));
        using var withinSkew = await ProbeClient.GetAsync(factory, ProbeModule.AuthenticatedPath, token);
        clock.Advance(TimeSpan.FromSeconds(15));
        using var expired = await ProbeClient.GetAsync(factory, ProbeModule.AuthenticatedPath, token);

        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        withinSkew.StatusCode.ShouldBe(HttpStatusCode.OK);
        await TokenValidationTests.ShouldBeUnauthorizedAsync(expired);
    }

    [Fact]
    public async Task Get_TokenNotYetValid_Returns401()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new ProbeWebFactory(timeProvider: clock);
        var token = ProbeClient.IssueAccessToken(factory);

        clock.Advance(TimeSpan.FromMinutes(-5));
        using var response = await ProbeClient.GetAsync(factory, ProbeModule.AuthenticatedPath, token);

        await TokenValidationTests.ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Get_ConfiguredAccessTokenLifetime_IsApplied()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new ProbeWebFactory(
            overrides: new Dictionary<string, string?> { ["Auth:Jwt:AccessTokenLifetime"] = "00:01:00", ["Auth:Jwt:ClockSkew"] = "00:00:00" },
            timeProvider: clock);
        var token = ProbeClient.IssueAccessToken(factory);

        clock.Advance(TimeSpan.FromSeconds(61));
        using var response = await ProbeClient.GetAsync(factory, ProbeModule.AuthenticatedPath, token);

        await TokenValidationTests.ShouldBeUnauthorizedAsync(response);
    }
}
