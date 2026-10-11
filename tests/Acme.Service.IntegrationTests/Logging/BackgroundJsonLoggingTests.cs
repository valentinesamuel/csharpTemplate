using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Security;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Modules;
using Acme.Service.Testing.Logging;
using Acme.Service.Testing;
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
/// Background work in the running host writes JSON lines with a correlation id (plan Phase 6, step 3; LOG-09): a job
/// enqueued with no ambient id gets a generated 32-hex id, a job enqueued inside a request keeps the request's
/// <c>X-Correlation-Id</c>; job lines never carry <c>requestId</c> or <c>requestMeta</c>.
/// </summary>
public sealed class BackgroundJsonLoggingTests(BackgroundJsonLoggingFixture fixture) : IClassFixture<BackgroundJsonLoggingFixture>
{
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Job_EnqueuedWithoutAmbientId_LineHasGeneratedCorrelationIdAndNoRequestContext()
    {
        var queue = fixture.Services.GetRequiredService<IJobQueue>();
        var marker = Guid.NewGuid().ToString("N");

        // No request, no activity: nothing for the queue to capture at enqueue time.
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            await queue.EnqueueAsync<BackgroundProbeJob, string>(marker, Ct);
        }
        finally
        {
            Activity.Current = previous;
        }

        var line = await WaitForJobLineAsync(marker);

        line.GetProperty("correlationId").GetString().ShouldNotBeNull().ShouldMatch("^[a-f0-9]{32}$");
        line.GetProperty("requestId").ValueKind.ShouldBe(JsonValueKind.Null);
        line.GetProperty("requestMeta").ValueKind.ShouldBe(JsonValueKind.Null);
        line.GetProperty("logger").GetString().ShouldBe(typeof(BackgroundProbeJob).FullName);
        fixture.Capture.ShouldHaveNoQueryInRequestPaths();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Job_EnqueuedDuringRequest_LineKeepsRequestCorrelationId()
    {
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(JobProbeModule.EnqueuePath, UriKind.Relative));
        request.Headers.Add("X-Correlation-Id", "job1");

        using var response = await client.SendAsync(request, Ct);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status202Accepted);
        var marker = (await response.Content.ReadFromJsonAsync<string>(Ct)).ShouldNotBeNull();
        var line = await WaitForJobLineAsync(marker);

        line.GetProperty("correlationId").GetString().ShouldBe("job1");
        line.GetProperty("requestId").ValueKind.ShouldBe(JsonValueKind.Null);
        line.GetProperty("requestMeta").ValueKind.ShouldBe(JsonValueKind.Null);
        fixture.Capture.ShouldHaveNoQueryInRequestPaths();
    }

    // The job runs on the in-memory worker after the enqueue returns.
    private async Task<JsonElement> WaitForJobLineAsync(string marker)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var lines = fixture.Capture.Parse().Where(line =>
                    line.GetProperty("meta") is { ValueKind: JsonValueKind.Object } meta
                    && meta.TryGetProperty("probeMarker", out var value)
                    && value.GetString() == marker)
                .ToList();
            if (lines.Count > 0 || stopwatch.Elapsed > _waitTimeout)
            {
                return lines.ShouldHaveSingleItem($"job line for marker {marker}");
            }

            await Task.Delay(20, Ct);
        }
    }
}

/// <summary>
/// The web host for <see cref="BackgroundJsonLoggingTests"/>: in-memory jobs, a <see cref="JsonLogCapture"/>, and the
/// test-only anonymous endpoint of <see cref="JobProbeModule"/>. The endpoint exists only in this factory, never in the
/// Host or the shared <see cref="ServiceWebFactory"/> (INV-12).
/// </summary>
public sealed class BackgroundJsonLoggingFixture : IAsyncDisposable
{
    private readonly JobProbeWebFactory _baseFactory = new();
    private readonly WebApplicationFactory<Program> _factory;

    public BackgroundJsonLoggingFixture() => _factory = _baseFactory.WithJsonLogCapture(Capture);

    /// <summary>Every line written by the host, formatted by the production formatter.</summary>
    public JsonLogCapture Capture { get; } = new();

    /// <summary>The services of the captured host (starts it).</summary>
    public IServiceProvider Services => _factory.Services;

    public HttpClient CreateClient() => _factory.CreateClient();

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    private sealed class JobProbeWebFactory : ServiceWebFactory
    {
        protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides { get; } =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Jobs:Provider"] = "InMemory",
            };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Same modules as the Host plus the job probe, whose endpoint MapModuleEndpoints maps.
                services.RemoveAll<ModuleRegistry>();
                services.AddModuleRegistry([.. HostModules.Create(), new JobProbeModule()]);
            });
        }
    }
}

/// <summary>
/// Test-only module mapping the anonymous <c>POST /__test/enqueue-job</c>, which enqueues a
/// <see cref="BackgroundProbeJob"/> with a fresh marker and returns the marker (202). Registered only by
/// <see cref="BackgroundJsonLoggingFixture"/>.
/// </summary>
internal sealed class JobProbeModule : IModule
{
    public const string EnqueuePath = "/__test/enqueue-job";

    public string Name => "test_job_probe";

    public IReadOnlyList<PermissionDefinition> Permissions { get; } = [];

    public void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
    }

    public void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(EnqueuePath, async (IJobQueue queue, CancellationToken cancellationToken) =>
            {
                var marker = Guid.NewGuid().ToString("N");
                await queue.EnqueueAsync<BackgroundProbeJob, string>(marker, cancellationToken);
                return TypedResults.Accepted((string?)null, marker);
            })
            .AllowAnonymous();
}

/// <summary>Test-only job that writes one Information line carrying its marker (<c>meta.probeMarker</c>).</summary>
internal sealed partial class BackgroundProbeJob(ILogger<BackgroundProbeJob> logger) : IJob<string>
{
    public Task ExecuteAsync(string payload, CancellationToken cancellationToken)
    {
        LogProbe(logger, payload);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information, Message = "Background probe {ProbeMarker}")]
    private static partial void LogProbe(ILogger logger, string probeMarker);
}
