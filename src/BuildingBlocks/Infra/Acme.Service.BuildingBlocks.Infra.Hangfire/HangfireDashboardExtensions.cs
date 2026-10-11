using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.Hangfire;

/// <summary>The optional Hangfire dashboard (D-21: Development only).</summary>
public static partial class HangfireDashboardExtensions
{
    /// <summary>Route of the dashboard.</summary>
    public const string DashboardPath = "/hangfire";

    /// <summary>
    /// Maps the Hangfire dashboard at <see cref="DashboardPath"/> only when <c>Jobs:Provider=Hangfire</c>,
    /// <c>Jobs:Hangfire:DashboardEnabled=true</c> AND the environment is Development (D-21). Outside Development an
    /// enabled dashboard is ignored with a warning. Safe to call whatever provider is selected.
    /// </summary>
    /// <remarks>
    /// The dashboard keeps Hangfire's default authorization (local requests only) and hides the storage connection
    /// string. It can delete and requeue jobs, which is why it never runs outside Development.
    /// </remarks>
    /// <returns>The dashboard endpoint, or null when it is not mapped.</returns>
    public static IEndpointConventionBuilder? MapHangfireDashboardIfEnabled(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        if (!services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(HangfireJobQueue)))
        {
            return null;
        }

        var options = services.GetRequiredService<IOptions<HangfireOptions>>().Value;
        if (!options.DashboardEnabled)
        {
            return null;
        }

        var environment = services.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment())
        {
            LogDashboardIgnored(services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HangfireDashboardExtensions)), environment.EnvironmentName);
            return null;
        }

        return endpoints.MapHangfireDashboard(
            DashboardPath,
            new DashboardOptions { DisplayStorageConnectionString = false },
            services.GetRequiredService<JobStorage>());
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Jobs:Hangfire:DashboardEnabled is ignored in the {EnvironmentName} environment; the dashboard is available only in Development (D-21)")]
    private static partial void LogDashboardIgnored(ILogger logger, string environmentName);
}
