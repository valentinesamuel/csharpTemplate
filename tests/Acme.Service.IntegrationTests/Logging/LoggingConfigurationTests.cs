using System.Text.Json;
using Acme.Service.Host.Observability;
using Acme.Service.Testing;
using Acme.Service.Testing.Logging;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Shouldly;

namespace Acme.Service.IntegrationTests.Logging;

/// <summary>
/// Shared logging setup (plan Phase 3, criteria 2 and 3, and the MEL EventId check of H-07): the in-process web host
/// writes S1-conformant JSON lines, configuration files define no sinks, and MEL event ids reach <c>eventId</c>.
/// </summary>
public sealed class LoggingConfigurationTests
{
    internal static readonly string[] Keys =
    [
        "timestamp", "level", "message", "requestId", "correlationId", "traceId", "spanId",
        "service", "environment", "logger", "eventId", "meta", "requestMeta", "exception",
    ];

    private static readonly Action<Microsoft.Extensions.Logging.ILogger, string, Exception?> _orderPlacedNamed =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1001, "OrderPlaced"), "Order {OrderId} placed");

    private static readonly Action<Microsoft.Extensions.Logging.ILogger, string, Exception?> _orderPlacedUnnamed =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1002), "Order {OrderId} placed");

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WebHost_WithCapture_WritesJsonLinesWithFixedSchema()
    {
        var capture = new JsonLogCapture();
        using var baseFactory = new ServiceWebFactory();
        using var factory = baseFactory.WithJsonLogCapture(capture);
        using var client = factory.CreateClient();

        // Unknown path: the fallback policy answers 401, and the summary is logged at Information (not filtered).
        using var response = await client.GetAsync(new Uri("/phase3-logging-probe", UriKind.Relative), TestContext.Current.CancellationToken);

        var lines = capture.RawLines;
        lines.Count.ShouldBeGreaterThanOrEqualTo(1);
        foreach (var raw in lines)
        {
            raw.ShouldEndWith("\n");
            raw.TrimEnd('\n').ShouldNotContain('\n');
            raw.ShouldNotContain('\r');

            var root = JsonLogCapture.ParseLine(raw);
            root.EnumerateObject().Select(p => p.Name).ShouldBe(Keys, raw);
            root.GetProperty("service").GetString().ShouldBe("acme-service", raw);
            root.GetProperty("environment").GetString().ShouldBe(ServiceWebFactory.EnvironmentName, raw);
        }
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void AppSettings_SerilogSection_DefinesNoSinksAndPinsDiagnosticsLevel(string fileName)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Host", "Acme.Service.Host", fileName);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var serilog = document.RootElement.GetProperty("Serilog");

        serilog.TryGetProperty("WriteTo", out _).ShouldBeFalse(fileName);
        serilog.TryGetProperty("Using", out _).ShouldBeFalse(fileName);
        serilog.GetProperty("MinimumLevel").GetProperty("Override")
            .GetProperty("Microsoft.AspNetCore.Diagnostics").GetString().ShouldBe("Warning", fileName);
    }

    [Fact]
    public void AppSettings_AllFiles_HaveNoSerilogSinks()
    {
        var directory = Path.Combine(RepositoryRoot(), "src", "Host", "Acme.Service.Host");
        var files = Directory.GetFiles(directory, "appsettings*.json");
        files.ShouldNotBeEmpty();

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.TryGetProperty("Serilog", out var serilog))
            {
                serilog.TryGetProperty("WriteTo", out _).ShouldBeFalse(file);
                serilog.TryGetProperty("Using", out _).ShouldBeFalse(file);
            }
        }
    }

    [Fact]
    public void MelLogger_ThroughLoggingSetup_WritesIntegerEventIdAndLogger()
    {
        var capture = new JsonLogCapture("acme-service", "Testing");
        using (var serilog = LoggingSetup.ConfigureConsole(new LoggerConfiguration(), "acme-service", "Testing")
                   .WriteTo.Sink(capture)
                   .CreateLogger())
        using (var loggerFactory = new SerilogLoggerFactory(serilog))
        {
            var logger = loggerFactory.CreateLogger("Acme.Service.Tests.EventIds");
            _orderPlacedNamed(logger, "o-1", null);
            _orderPlacedUnnamed(logger, "o-2", null);
        }

        var events = capture.Parse();
        events.Count.ShouldBe(2);
        events[0].GetProperty("eventId").GetInt32().ShouldBe(1001);
        events[1].GetProperty("eventId").GetInt32().ShouldBe(1002);
        foreach (var root in events)
        {
            root.EnumerateObject().Select(p => p.Name).ShouldBe(Keys);
            root.GetProperty("logger").GetString().ShouldBe("Acme.Service.Tests.EventIds");
            root.GetProperty("meta").EnumerateObject().Select(p => p.Name).ShouldBe(["orderId"]);
        }
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Acme.Service.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find Acme.Service.slnx above '{AppContext.BaseDirectory}'.");
    }
}
