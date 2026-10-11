using System.Globalization;
using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Acme.Service.BuildingBlocks.Web.Correlation;
using Acme.Service.Host.Configuration;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace Acme.Service.Host.Observability;

/// <summary>
/// Serilog (console: plain text in Development, compact JSON elsewhere; OTLP logs when
/// <c>Observability:Otlp:Endpoint</c> is set) and OpenTelemetry traces and metrics.
/// </summary>
internal static class ObservabilityExtensions
{
    private const string DevelopmentOutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {CorrelationId} {Message:lj}{NewLine}{Exception}";

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
    public static WebApplication UseRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.Logger = app.Services.GetService<Serilog.ILogger>();
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
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
            };
        });

        return app;
    }

    private static void ConfigureSerilog(LoggerConfiguration logger, IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = ReadSettings(configuration);

        logger
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext();

        if (environment.IsDevelopment())
        {
            logger.WriteTo.Console(outputTemplate: DevelopmentOutputTemplate, formatProvider: CultureInfo.InvariantCulture);
        }
        else
        {
            logger.WriteTo.Console(new RenderedCompactJsonFormatter());
        }

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

    private static ObservabilityOptions ReadSettings(IConfiguration configuration) =>
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
