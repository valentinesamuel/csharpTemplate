using System.Net;
using System.Text.Json;
using Acme.Service.Testing;
using Shouldly;

namespace Acme.Service.IntegrationTests;

public sealed class HostSmokeTests(ServiceWebFactory factory) : IClassFixture<ServiceWebFactory>
{
    private const string CorrelationHeader = "X-Correlation-Id";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetLive_ProcessRunning_Returns200()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetReady_PostgresNotConfigured_Returns503()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_ValidCorrelationId_EchoesIt()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add(CorrelationHeader, "test-123");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues(CorrelationHeader).ShouldBe(["test-123"]);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("bad id!")]
    [InlineData("a/b")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123x")]
    public async Task Get_InvalidCorrelationId_ReplacesWithGeneratedId(string inbound)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(CorrelationHeader, inbound);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var echoed = response.Headers.GetValues(CorrelationHeader).ShouldHaveSingleItem();
        echoed.ShouldNotBe(inbound);
        echoed.ShouldMatch("^[a-f0-9]{32}$");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_UnknownRouteAnonymous_Returns401ProblemDetails()
    {
        // Fail closed (INV-12): the fallback policy also applies to requests that match no endpoint (WH-16), so an
        // anonymous caller learns nothing about which routes exist.
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldContain(h => h.Scheme == "Bearer");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var problem = JsonDocument.Parse(body);
        var root = problem.RootElement;
        root.GetProperty("status").GetInt32().ShouldBe(401);
        root.GetProperty("code").GetString().ShouldBe("unauthorized");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("correlationId").GetString()
            .ShouldBe(response.Headers.GetValues(CorrelationHeader).ShouldHaveSingleItem());
    }
}
