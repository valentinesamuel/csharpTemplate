using System.Collections.Concurrent;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Modules;
using Acme.Service.Testing;
using Acme.Service.Testing.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.IntegrationTests.Fixtures;

/// <summary>
/// The real web host over one test database for the P19 suites: the Host's modules plus <see cref="ClaimsProbeModule"/>
/// (test-only <c>GET /test/claims</c>), a fast outbox poll, high rate limits unless <paramref name="overrides"/> set them,
/// emails captured unless the overrides select a real notification provider, and a record of the endpoint each request
/// matched (<see cref="MatchedEndpoints"/>).
/// </summary>
internal class IntegrationWebFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? overrides = null,
    Action<IServiceCollection>? configureServices = null) : ServiceWebFactory
{
    /// <summary>Test-only request header naming a request whose matched endpoint is recorded.</summary>
    public const string ProbeHeader = "X-Test-Probe-Id";

    public string ConnectionString => connectionString;

    /// <summary>Route pattern matched by each request that carried <see cref="ProbeHeader"/>, by header value.</summary>
    public ConcurrentDictionary<string, string?> MatchedEndpoints { get; } = new(StringComparer.Ordinal);

    /// <summary>The emails sent by the host (only when notifications are captured).</summary>
    public CapturingNotificationSender Emails => Services.GetRequiredService<CapturingNotificationSender>();

    private bool CapturesEmails => overrides is null
        || !overrides.TryGetValue("Notifications:Provider", out var provider)
        || string.IsNullOrEmpty(provider);

    protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides
    {
        get
        {
            var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Outbox:PollingInterval"] = "00:00:00.100",
                ["Auth:RateLimiting:Credentials:PermitLimit"] = "1000",
                ["Auth:RateLimiting:Email:PermitLimit"] = "1000",
            };
            foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            {
                settings[key] = value;
            }

            return settings;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            if (CapturesEmails)
            {
                new CapturingNotificationsRegistration().Register(services, new ConfigurationBuilder().Build());
            }

            // Same modules as the Host plus the claims probe, whose endpoint MapModuleEndpoints maps.
            services.RemoveAll<ModuleRegistry>();
            services.AddModuleRegistry([.. HostModules.Create(), new ClaimsProbeModule()]);

            services.AddSingleton<IStartupFilter>(new EndpointRecorderStartupFilter(MatchedEndpoints));
            configureServices?.Invoke(services);
        });
    }

    /// <summary>
    /// Outermost middleware: after the pipeline ran, records the route pattern routing selected for the request, so a
    /// test can tell "401 from the endpoint's authorization" from "401 because no endpoint matched".
    /// </summary>
    private sealed class EndpointRecorderStartupFilter(ConcurrentDictionary<string, string?> matched) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                await nextMiddleware(context).ConfigureAwait(false);
                var probe = context.Request.Headers[ProbeHeader].ToString();
                if (probe.Length > 0)
                {
                    matched[probe] = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
                }
            });
            next(app);
        };
    }
}
