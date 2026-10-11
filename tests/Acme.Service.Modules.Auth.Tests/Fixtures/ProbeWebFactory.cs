using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Modules;
using Acme.Service.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>
/// The real web host plus <see cref="ProbeModule"/>'s endpoints, an optional database, configuration overrides and an
/// optional manual clock. Without a connection string the host runs with no reachable Auth database, which is enough
/// for stateless token validation (INV-13).
/// </summary>
internal sealed class ProbeWebFactory(
    string? connectionString = null,
    IReadOnlyDictionary<string, string?>? overrides = null,
    TimeProvider? timeProvider = null) : ServiceWebFactory
{
    /// <summary>The Auth database, when one is configured.</summary>
    public string ConnectionString => connectionString ?? throw new InvalidOperationException("No database is configured.");

    protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides
    {
        get
        {
            var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (connectionString is not null)
            {
                settings["ConnectionStrings:Postgres"] = connectionString;
            }

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
            // Same modules as the Host plus the probe module, whose endpoints MapModuleEndpoints maps.
            services.RemoveAll<ModuleRegistry>();
            services.AddModuleRegistry([.. HostModules.Create(), new ProbeModule()]);

            if (timeProvider is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(timeProvider));
            }
        });
    }
}
