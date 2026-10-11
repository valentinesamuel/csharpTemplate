using System.Net;
using Acme.Service.Host.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Acme.Service.Host.Http;

/// <summary>
/// Trusts <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> only from the proxies configured under
/// <c>Http:ForwardedHeaders</c>. When none are configured only loopback is trusted, and a warning is
/// logged outside Development.
/// </summary>
internal static partial class ForwardedHeadersSetup
{
    public static IServiceCollection AddForwardedHeadersSetup(this IServiceCollection services, IConfiguration configuration)
    {
        HttpOptions.Register(services, configuration);

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<HttpOptions>>((options, http) =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                var settings = http.Value.ForwardedHeaders;
                if (settings.KnownNetworks.Count == 0 && settings.KnownProxies.Count == 0)
                {
                    // Keep the framework defaults (loopback only).
                    return;
                }

                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                foreach (var network in settings.KnownNetworks)
                {
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }

                foreach (var proxy in settings.KnownProxies)
                {
                    options.KnownProxies.Add(IPAddress.Parse(proxy));
                }
            });

        return services;
    }

    /// <summary>Adds the forwarded-headers middleware. Must be the first middleware in the pipeline.</summary>
    public static WebApplication UseForwardedHeadersSetup(this WebApplication app)
    {
        app.UseForwardedHeaders();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var settings = app.Services.GetRequiredService<IOptions<HttpOptions>>().Value.ForwardedHeaders;
            if (!app.Environment.IsDevelopment()
                && settings.KnownNetworks.Count == 0
                && settings.KnownProxies.Count == 0)
            {
                LogNoTrustedProxies(app.Logger, $"{HttpOptions.SectionName}:{nameof(HttpOptions.ForwardedHeaders)}");
            }
        });

        return app;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{ConfigKey} has no KnownNetworks or KnownProxies; forwarded headers are trusted from loopback only, so client IP and scheme behind a proxy will be wrong")]
    private static partial void LogNoTrustedProxies(ILogger logger, string configKey);
}
