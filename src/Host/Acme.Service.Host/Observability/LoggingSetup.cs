using Serilog;

namespace Acme.Service.Host.Observability;

/// <summary>
/// The single definition of console logging (LOG-06, LOG-07): <c>Enrich.FromLogContext</c>, destructuring limits and a
/// stdout console sink that writes every event as one JSON line through <see cref="JsonLogFormatter"/>. Used by the web
/// logger, the bootstrap (startup-failure) logger and the command verbs.
/// </summary>
internal static class LoggingSetup
{
    /// <summary>Service name used by the bootstrap logger when <c>Observability__ServiceName</c> is not set.</summary>
    public const string DefaultServiceName = "acme-service";

    private const string ServiceNameVariable = "Observability__ServiceName";
    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";
    private const string DotNetEnvironmentVariable = "DOTNET_ENVIRONMENT";

    private const int MaximumDestructuringDepth = 5;
    private const int MaximumStringLength = 8192;
    private const int MaximumCollectionCount = 100;

    /// <summary>
    /// Adds log-context enrichment, destructuring limits and the JSON console sink (stdout only). Call after
    /// <c>ReadFrom.Configuration</c> so these settings win over configuration.
    /// </summary>
    /// <param name="logger">The configuration to extend.</param>
    /// <param name="service">Value of the <c>service</c> field; blank becomes <c>null</c>.</param>
    /// <param name="environment">Value of the <c>environment</c> field; blank becomes <c>null</c>.</param>
    public static LoggerConfiguration ConfigureConsole(LoggerConfiguration logger, string? service, string? environment)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return logger
            .Enrich.FromLogContext()
            .Destructure.ToMaximumDepth(MaximumDestructuringDepth)
            .Destructure.ToMaximumStringLength(MaximumStringLength)
            .Destructure.ToMaximumCollectionCount(MaximumCollectionCount)
            .WriteTo.Console(new JsonLogFormatter(service, environment));
    }

    /// <summary>
    /// Creates the startup-failure logger used by <c>Program</c> before configuration is available: environment from
    /// <c>ASPNETCORE_ENVIRONMENT</c>, then <c>DOTNET_ENVIRONMENT</c>, else <c>Production</c>; service from
    /// <c>Observability__ServiceName</c>, else <see cref="DefaultServiceName"/>.
    /// </summary>
    public static Serilog.Core.Logger CreateBootstrapLogger()
    {
        var environment = NonBlank(Environment.GetEnvironmentVariable(AspNetCoreEnvironmentVariable))
            ?? NonBlank(Environment.GetEnvironmentVariable(DotNetEnvironmentVariable))
            ?? Environments.Production;
        var service = NonBlank(Environment.GetEnvironmentVariable(ServiceNameVariable)) ?? DefaultServiceName;

        return ConfigureConsole(new LoggerConfiguration(), service, environment).CreateLogger();
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
