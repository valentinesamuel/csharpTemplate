using System.Text.Json;
using Acme.Service.Host.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace Acme.Service.Testing.Logging;

/// <summary>
/// In-memory <see cref="ILogEventSink"/> that formats every event with the production <see cref="JsonLogFormatter"/>
/// into a thread-safe list of lines (D-06). Register it on a host with
/// <see cref="JsonLogCaptureExtensions.WithJsonLogCapture"/>; the Host picks it up through <c>ReadFrom.Services</c>.
/// Per host, so parallel test classes do not see each other's lines.
/// </summary>
public sealed class JsonLogCapture : ILogEventSink
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];
    private JsonLogFormatter? _formatter;

    /// <summary>Creates a capture whose formatter is bound when the host resolves it (service and environment of the host).</summary>
    public JsonLogCapture()
    {
    }

    /// <summary>Creates a capture with an explicit <c>service</c> and <c>environment</c> (for loggers built without a host).</summary>
    public JsonLogCapture(string? service, string? environment) => _formatter = new JsonLogFormatter(service, environment);

    /// <summary>Snapshot of the captured output, one entry per event, each exactly as written (including the line terminator).</summary>
    public IReadOnlyList<string> RawLines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>Snapshot of the captured lines without their line terminator.</summary>
    public IReadOnlyList<string> Lines => [.. RawLines.Select(line => line.TrimEnd('\n'))];

    /// <summary>Parses every captured line with a strict JSON parser (throws <see cref="JsonException"/> on an invalid line).</summary>
    public IReadOnlyList<JsonElement> Parse() => [.. Lines.Select(ParseLine)];

    /// <summary>Removes every captured line.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var formatter = _formatter ?? new JsonLogFormatter(null, null);
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        formatter.Format(logEvent, writer);
        var line = writer.ToString();

        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    /// <summary>
    /// Asserts that no captured line has a <c>requestMeta.path</c> containing <c>?</c> (LOG-01: the path is
    /// <c>PathBase + Path</c>, never the query string). Every line must parse; lines without <c>requestMeta</c> are skipped.
    /// </summary>
    public void ShouldHaveNoQueryInRequestPaths()
    {
        foreach (var raw in Lines)
        {
            var line = ParseLine(raw);
            if (line.TryGetProperty("requestMeta", out var requestMeta)
                && requestMeta.ValueKind == JsonValueKind.Object
                && requestMeta.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.String)
            {
                path.GetString().ShouldNotBeNull().ShouldNotContain('?', raw);
            }
        }
    }

    /// <summary>Parses one line with the strict default options (no comments, no trailing commas).</summary>
    public static JsonElement ParseLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    internal JsonLogCapture Bind(IConfiguration configuration, IHostEnvironment environment)
    {
        var serviceName = ObservabilityExtensions.ReadSettings(configuration).ServiceName;
        _formatter ??= new JsonLogFormatter(serviceName, environment.EnvironmentName);
        return this;
    }
}

/// <summary>Registers a <see cref="JsonLogCapture"/> on a test host.</summary>
public static class JsonLogCaptureExtensions
{
    /// <summary>
    /// Returns a derived factory whose Serilog pipeline also writes to <paramref name="capture"/>. Unless created with
    /// an explicit service and environment, the capture's formatter uses the same <c>Observability:ServiceName</c> and
    /// environment name as the Host's console sink.
    /// </summary>
    public static WebApplicationFactory<TEntryPoint> WithJsonLogCapture<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        JsonLogCapture capture)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(capture);

        return factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddJsonLogCapture(capture)));
    }

    /// <summary>
    /// Registers <paramref name="capture"/> as an <see cref="ILogEventSink"/> on a host's services (for factories that
    /// expose a service hook instead of being wrapped by <see cref="WithJsonLogCapture"/>). Same binding rules.
    /// </summary>
    public static IServiceCollection AddJsonLogCapture(this IServiceCollection services, JsonLogCapture capture)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(capture);

        return services.AddSingleton<ILogEventSink>(provider => capture.Bind(
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IHostEnvironment>()));
    }
}
