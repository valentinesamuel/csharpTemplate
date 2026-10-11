using Acme.Service.BuildingBlocks.Logging;
using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Acme.Service.BuildingBlocks.Web.Correlation;
using Acme.Service.Host.Configuration;
using Microsoft.AspNetCore.Diagnostics;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

namespace Acme.Service.Host.Observability;

/// <summary>
/// Serilog (console: one JSON line per event in every environment, defined by <see cref="LoggingSetup"/>; OTLP logs
/// when <c>Observability:Otlp:Endpoint</c> is set) and OpenTelemetry traces and metrics.
/// </summary>
internal static class ObservabilityExtensions
{
    // Npgsql emits traces and metrics natively under this name; no extra package is needed.
    private const string NpgsqlTelemetryName = "Npgsql";

    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        ObservabilityOptions.Register(builder.Services, builder.Configuration);

        // preserveStaticLogger: the static Log.Logger stays the bootstrap logger used by Program for
        // startup failures, and several hosts (WebApplicationFactory) can coexist in one process.
        builder.Services.AddSerilog(
            (services, logger) => ConfigureSerilog(
                logger,
                services,
                services.GetRequiredService<IConfiguration>(),
                services.GetRequiredService<IHostEnvironment>()),
            preserveStaticLogger: true);

        var settings = ReadSettings(builder.Configuration);
        var otlpEndpoint = OtlpEndpoint(settings);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                // A blank name is rejected by ValidateOnStart with the key named; do not fail here.
                if (!string.IsNullOrWhiteSpace(settings.ServiceName))
                {
                    resource.AddService(settings.ServiceName);
                }
            })
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(NpgsqlTelemetryName)
                .AddSource(MessagingTelemetry.Name))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(NpgsqlTelemetryName)
                .AddMeter(MessagingTelemetry.Name));

        if (otlpEndpoint is not null)
        {
            otel.WithTracing(tracing => tracing.AddOtlpExporter(o => ConfigureExporter(o, otlpEndpoint)))
                .WithMetrics(metrics => metrics.AddOtlpExporter(o => ConfigureExporter(o, otlpEndpoint)));
        }

        return builder;
    }

    /// <summary>
    /// One summary event per request. Only method, path (never the query string), status and timing are
    /// logged; no headers (so never Authorization) are captured (INV-28).
    /// </summary>
    /// <remarks>
    /// The summary is written through the DI <see cref="Serilog.ILogger"/> and does not see Microsoft.Extensions.Logging
    /// scopes, so its request context is set explicitly (S5): <c>CorrelationId</c> and <c>RequestId</c> through the
    /// diagnostic context, the template properties plus the dotted <c>Http.*</c> properties (the JSON
    /// <c>requestMeta</c>) through <see cref="Serilog.AspNetCore.RequestLoggingOptions.GetMessageTemplateProperties"/>.
    /// The path comes from <see cref="RequestLogValues.Path"/>, identical to the one on every line inside the request.
    /// </remarks>
    public static WebApplication UseRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.Logger = app.Services.GetService<Serilog.ILogger>();
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.GetMessageTemplateProperties = SummaryProperties;
            options.GetLevel = (http, _, exception) =>
                exception is not null || http.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Error
                    : http.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                        ? LogEventLevel.Debug
                        : LogEventLevel.Information;
            options.EnrichDiagnosticContext = (diagnostics, http) =>
            {
                var correlationId = http.RequestServices.GetService<ICorrelationIdAccessor>()?.CorrelationId;
                if (correlationId is not null)
                {
                    diagnostics.Set(CorrelationId.PropertyName, correlationId);
                }

                diagnostics.Set(LogPropertyNames.RequestId, http.TraceIdentifier);
            };
        });

        return app;
    }

    // The raw-target path Serilog passes in is ignored: the S4 path rule (PathBase + Path, no query) applies.
    private static IEnumerable<LogEventProperty> SummaryProperties(HttpContext http, string _, double elapsedMs, int statusCode)
    {
        var path = RequestLogValues.Path(http.Request);
        var isSuccess = RequestLogValues.IsSuccess(statusCode, http.Features.Get<IExceptionHandlerFeature>() is not null);

        return
        [
            new LogEventProperty(LogPropertyNames.RequestMethod, new ScalarValue(http.Request.Method)),
            new LogEventProperty(LogPropertyNames.RequestPath, new ScalarValue(path)),
            new LogEventProperty(LogPropertyNames.StatusCode, new ScalarValue(statusCode)),
            new LogEventProperty(LogPropertyNames.Elapsed, new ScalarValue(elapsedMs)),
            new LogEventProperty(LogPropertyNames.HttpMethod, new ScalarValue(http.Request.Method)),
            new LogEventProperty(LogPropertyNames.HttpPath, new ScalarValue(path)),
            new LogEventProperty(LogPropertyNames.HttpStatusCode, new ScalarValue(statusCode)),
            new LogEventProperty(LogPropertyNames.HttpDurationMs, new ScalarValue(elapsedMs)),
            new LogEventProperty(LogPropertyNames.HttpIsSuccess, new ScalarValue(isSuccess)),
        ];
    }

    private static void ConfigureSerilog(
        LoggerConfiguration logger,
        IServiceProvider services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var settings = ReadSettings(configuration);

        // ReadFrom.Services adds ILogEventSink/ILogEventEnricher registrations from DI (the test capture seam, D-06).
        logger
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services);

        // The JSON service field and the OTLP service.name come from the same setting (LOG-13).
        LoggingSetup.ConfigureConsole(
            logger,
            string.IsNullOrWhiteSpace(settings.ServiceName) ? null : settings.ServiceName,
            environment.EnvironmentName);

        var otlpEndpoint = OtlpEndpoint(settings);
        if (otlpEndpoint is not null)
        {
            logger.WriteTo.OpenTelemetry(sink =>
            {
                sink.Endpoint = otlpEndpoint.ToString();
                sink.Protocol = OtlpProtocol.Grpc;
                if (!string.IsNullOrWhiteSpace(settings.ServiceName))
                {
                    sink.ResourceAttributes["service.name"] = settings.ServiceName;
                }
            });
        }
    }

    internal static ObservabilityOptions ReadSettings(IConfiguration configuration) =>
        configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();

    private static Uri? OtlpEndpoint(ObservabilityOptions settings) =>
        !string.IsNullOrWhiteSpace(settings.Otlp.Endpoint) && ObservabilityOptions.IsHttpUri(settings.Otlp.Endpoint)
            ? new Uri(settings.Otlp.Endpoint)
            : null;

    private static void ConfigureExporter(OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.Grpc;
    }
}
