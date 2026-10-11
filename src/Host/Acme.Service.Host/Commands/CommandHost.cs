using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Observability;
using Acme.Service.Host.Security;
using Serilog;

namespace Acme.Service.Host.Commands;

/// <summary>
/// The minimal host shared by the command verbs (§0.9): configuration, logging, Data Protection, the module list and
/// <see cref="IModule.AddPersistence"/> for every module. It needs no JWT, SMTP or blob settings.
/// </summary>
internal static class CommandHost
{
    /// <summary>
    /// Creates the builder. <paramref name="configurationArgs"/> are <c>--Section:Key=value</c> overrides passed to the
    /// host configuration.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(IReadOnlyList<string> configurationArgs, IModule[] modules)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([.. configurationArgs]);
        builder.Services.AddSerilog(
            (services, logger) =>
            {
                var configuration = services.GetRequiredService<IConfiguration>();
                var serviceName = ObservabilityExtensions.ReadSettings(configuration).ServiceName;
                LoggingSetup.ConfigureConsole(
                    logger.ReadFrom.Configuration(configuration),
                    string.IsNullOrWhiteSpace(serviceName) ? null : serviceName,
                    services.GetRequiredService<IHostEnvironment>().EnvironmentName);
            },
            preserveStaticLogger: true);

        builder.Services.AddDataProtectionSetup();
        builder.Services.AddModuleRegistry(modules);
        foreach (var module in modules)
        {
            module.AddPersistence(builder.Services, builder.Configuration);
        }

        return builder;
    }

    /// <summary>True when every argument has the <c>--Section:Key=value</c> form.</summary>
    public static bool AreConfigurationArgs(IEnumerable<string> args) =>
        args.All(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=', StringComparison.Ordinal));
}
