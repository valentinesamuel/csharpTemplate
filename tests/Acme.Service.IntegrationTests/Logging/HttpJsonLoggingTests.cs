using System.Diagnostics;
using System.Text.Json;
using Acme.Service.BuildingBlocks.Security;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Modules;
using Acme.Service.Testing;
using Acme.Service.Testing.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Acme.Service.IntegrationTests.Logging;

/// <summary>
/// HTTP request context in the JSON log lines (plan Phase 4, criteria 1 to 6): every line inside a request carries
/// <c>requestId</c>, <c>correlationId</c> and <c>requestMeta.method/path</c>; the request-summary event carries
/// <c>requestMeta.statusCode/durationInMs/isSuccess</c>; no query string or header value is ever logged.
/// </summary>
public sealed class HttpJsonLoggingTests(HttpJsonLoggingFixture fixture) : IClassFixture<HttpJsonLoggingFixture>
{
    private const string CorrelationHeader = "X-Correlation-Id";
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] _requestMetaKeys = ["method", "path", "statusCode", "durationInMs", "isSuccess"];

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Post_LogMetaProbe_HandlerAndSummaryLinesCarryRequestContext()
    {
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(LoggingProbeModule.LogMetaPath, UriKind.Relative));
        request.Headers.Add(CorrelationHeader, "test1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status201Created);
        var summary = await WaitForSummaryAsync("test1");
        var handler = fixture.Capture.Parse().Single(line =>
            CorrelationIdOf(line) == "test1"
            && line.GetProperty("meta").ValueKind == JsonValueKind.Object
            && line.GetProperty("meta").TryGetProperty("orderId", out _));

        var meta = handler.GetProperty("meta");
        meta.EnumerateObject().Select(p => p.Name).ShouldBe(["orderId", "email", "totalAmount"]);
        meta.GetProperty("orderId").GetString().ShouldBe("o-42");
        meta.GetProperty("email").GetString().ShouldBe("a@b.com");
        meta.GetProperty("totalAmount").GetDecimal().ShouldBe(19.99m);
        handler.GetProperty("correlationId").GetString().ShouldBe("test1");
        var requestId = handler.GetProperty("requestId").GetString();
        requestId.ShouldNotBeNullOrWhiteSpace();
        var traceId = handler.GetProperty("traceId").GetString();
        traceId.ShouldNotBeNull();
        traceId.ShouldMatch("^[0-9a-f]{32}$");
        var handlerMeta = handler.GetProperty("requestMeta");
        handlerMeta.EnumerateObject().Select(p => p.Name).ShouldBe(_requestMetaKeys);
        handlerMeta.GetProperty("method").GetString().ShouldBe("POST");
        handlerMeta.GetProperty("path").GetString().ShouldBe(LoggingProbeModule.LogMetaPath);
        handlerMeta.GetProperty("statusCode").ValueKind.ShouldBe(JsonValueKind.Null);
        handlerMeta.GetProperty("durationInMs").ValueKind.ShouldBe(JsonValueKind.Null);
        handlerMeta.GetProperty("isSuccess").ValueKind.ShouldBe(JsonValueKind.Null);

        var summaryMeta = summary.GetProperty("requestMeta");
        summaryMeta.EnumerateObject().Select(p => p.Name).ShouldBe(_requestMetaKeys);
        summaryMeta.GetProperty("method").GetString().ShouldBe("POST");
        summaryMeta.GetProperty("path").GetString().ShouldBe(LoggingProbeModule.LogMetaPath);
        summaryMeta.GetProperty("statusCode").GetInt32().ShouldBe(StatusCodes.Status201Created);
        summaryMeta.GetProperty("durationInMs").GetDouble().ShouldBeGreaterThan(0);
        summaryMeta.GetProperty("isSuccess").GetBoolean().ShouldBeTrue();
        summary.GetProperty("level").GetString().ShouldBe("Information");
        summary.GetProperty("message").GetString().ShouldStartWith($"HTTP POST {LoggingProbeModule.LogMetaPath} responded 201 in ");
        summary.GetProperty("correlationId").GetString().ShouldBe("test1");
        summary.GetProperty("requestId").GetString().ShouldBe(requestId);
        summary.GetProperty("traceId").GetString().ShouldBe(traceId);
        var summaryCallSite = summary.GetProperty("meta");
        summaryCallSite.ValueKind.ShouldBe(JsonValueKind.Object);
        summaryCallSite.EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetLive_Healthy_SummaryIsDebugAndSuccessful()
    {
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add(CorrelationHeader, "test2");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status200OK);
        var summary = await WaitForSummaryAsync("test2");
        summary.GetProperty("level").GetString().ShouldBe("Debug");
        summary.GetProperty("requestMeta").GetProperty("path").GetString().ShouldBe("/health/live");
        summary.GetProperty("requestMeta").GetProperty("statusCode").GetInt32().ShouldBe(StatusCodes.Status200OK);
        summary.GetProperty("requestMeta").GetProperty("isSuccess").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_UnknownRouteAnonymous_SummaryIs401AndSharesProblemTraceId()
    {
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/nope", UriKind.Relative));
        request.Headers.Add(CorrelationHeader, "test3");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status401Unauthorized);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var problem = JsonDocument.Parse(body);
        var problemTraceId = problem.RootElement.GetProperty("traceId").GetString();
        var problemRequestId = problem.RootElement.GetProperty("requestId").GetString();

        var summary = await WaitForSummaryAsync("test3");
        summary.GetProperty("requestMeta").GetProperty("statusCode").GetInt32().ShouldBe(StatusCodes.Status401Unauthorized);
        summary.GetProperty("requestMeta").GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
        summary.GetProperty("traceId").GetString().ShouldBe(problemTraceId);
        summary.GetProperty("requestId").GetString().ShouldBe(problemRequestId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_ThrowingHandler_SummaryIsErrorAndExceptionLoggedOnce()
    {
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(LoggingProbeModule.ThrowPath, UriKind.Relative));
        request.Headers.Add(CorrelationHeader, "test4");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status500InternalServerError);
        var summary = await WaitForSummaryAsync("test4");
        summary.GetProperty("level").GetString().ShouldBe("Error");
        summary.GetProperty("requestMeta").GetProperty("statusCode").GetInt32().ShouldBe(StatusCodes.Status500InternalServerError);
        summary.GetProperty("requestMeta").GetProperty("isSuccess").GetBoolean().ShouldBeFalse();

        var withException = fixture.Capture.Parse()
            .Where(line => CorrelationIdOf(line) == "test4" && line.GetProperty("exception").ValueKind != JsonValueKind.Null)
            .ToList();
        var exceptionLine = withException.ShouldHaveSingleItem();
        exceptionLine.GetProperty("exception").GetString().ShouldNotBeNull().ShouldContain(nameof(InvalidOperationException));
        exceptionLine.GetProperty("logger").GetString().ShouldBe("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware");
        exceptionLine.GetProperty("requestMeta").GetProperty("path").GetString().ShouldBe(LoggingProbeModule.ThrowPath);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_QueryTokenAndAuthorizationHeader_NeverLogged()
    {
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live?token=SECRET123", UriKind.Relative));
        request.Headers.Add(CorrelationHeader, "test5");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer SECRET456");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var summary = await WaitForSummaryAsync("test5");
        summary.GetProperty("requestMeta").GetProperty("path").GetString().ShouldBe("/health/live");
        summary.GetProperty("message").GetString().ShouldNotBeNull().ShouldNotContain("?");

        foreach (var raw in fixture.Capture.Lines)
        {
            raw.ShouldNotContain("SECRET123");
            raw.ShouldNotContain("SECRET456");
            raw.ShouldNotContain("token=");

            var line = JsonLogCapture.ParseLine(raw);
            if (CorrelationIdOf(line) == "test5")
            {
                line.GetProperty("message").GetString().ShouldNotBeNull().ShouldNotContain('?', raw);
            }
        }

        fixture.Capture.ShouldHaveNoQueryInRequestPaths();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AllCapturedLines_AfterRequests_AreValidJsonWithFixedSchema()
    {
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(LoggingProbeModule.LogMetaPath, UriKind.Relative));
        request.Headers.Add(CorrelationHeader, "test6");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await WaitForSummaryAsync("test6");

        var lines = fixture.Capture.RawLines;
        lines.ShouldNotBeEmpty();
        foreach (var raw in lines)
        {
            raw.ShouldEndWith("\n");
            raw.TrimEnd('\n').ShouldNotContain('\n');
            raw.ShouldNotContain('\r');

            var root = JsonLogCapture.ParseLine(raw);
            root.EnumerateObject().Select(p => p.Name).ShouldBe(LoggingConfigurationTests.Keys, raw);
        }
    }

    private static string? CorrelationIdOf(JsonElement line) =>
        line.GetProperty("correlationId").ValueKind == JsonValueKind.String ? line.GetProperty("correlationId").GetString() : null;

    private static bool IsSummary(JsonElement line) =>
        line.GetProperty("requestMeta") is { ValueKind: JsonValueKind.Object } requestMeta
        && requestMeta.GetProperty("statusCode").ValueKind == JsonValueKind.Number;

    // The summary is written when the pipeline unwinds, which can be after the test client has the response.
    private async Task<JsonElement> WaitForSummaryAsync(string correlationId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var summaries = fixture.Capture.Parse().Where(l => IsSummary(l) && CorrelationIdOf(l) == correlationId).ToList();
            if (summaries.Count > 0 || stopwatch.Elapsed > _waitTimeout)
            {
                return summaries.ShouldHaveSingleItem($"summary line for correlation id {correlationId}");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>
/// The web host for <see cref="HttpJsonLoggingTests"/>: Debug minimum level (so <c>/health*</c> summaries are kept),
/// a <see cref="JsonLogCapture"/>, and the test-only anonymous endpoints of <see cref="LoggingProbeModule"/>. The
/// endpoints exist only in this factory, never in the Host or the shared <see cref="ServiceWebFactory"/> (INV-12).
/// </summary>
public sealed class HttpJsonLoggingFixture : IAsyncDisposable
{
    private readonly ProbeWebFactory _baseFactory = new();
    private readonly WebApplicationFactory<Program> _factory;

    public HttpJsonLoggingFixture() => _factory = _baseFactory.WithJsonLogCapture(Capture);

    /// <summary>Every line written by the host, formatted by the production formatter.</summary>
    public JsonLogCapture Capture { get; } = new();

    public HttpClient CreateClient() => _factory.CreateClient();

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    private sealed class ProbeWebFactory : ServiceWebFactory
    {
        protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides { get; } =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Serilog:MinimumLevel:Default"] = "Debug",
            };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Same modules as the Host plus the logging probe, whose endpoints MapModuleEndpoints maps.
                services.RemoveAll<ModuleRegistry>();
                services.AddModuleRegistry([.. HostModules.Create(), new LoggingProbeModule()]);
            });
        }
    }
}

/// <summary>
/// Test-only module mapping the anonymous logging probes <c>POST /__test/log-meta</c> (one call-site line, 201) and
/// <c>GET /__test/throw</c> (an unhandled <see cref="InvalidOperationException"/>). Registered only by
/// <see cref="HttpJsonLoggingFixture"/>.
/// </summary>
internal sealed class LoggingProbeModule : IModule
{
    public const string LogMetaPath = "/__test/log-meta";
    public const string ThrowPath = "/__test/throw";

    private static readonly Action<ILogger, string, string, decimal, Exception?> _probe =
        LoggerMessage.Define<string, string, decimal>(LogLevel.Information, new EventId(4001, "LoggingProbe"), "Probe {OrderId} {Email} {TotalAmount}");

    public string Name => "test_logging_probe";

    public IReadOnlyList<PermissionDefinition> Permissions { get; } = [];

    public void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
    }

    public void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(LogMetaPath, (ILogger<LoggingProbeModule> logger) =>
            {
                _probe(logger, "o-42", "a@b.com", 19.99m, null);
                return TypedResults.StatusCode(StatusCodes.Status201Created);
            })
            .AllowAnonymous();

        endpoints.MapGet(ThrowPath, IResult () => throw new InvalidOperationException("Logging probe failure"))
            .AllowAnonymous();
    }
}
