using Acme.Service.Testing;
using Acme.Service.Testing.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Orders.Tests.Fixtures;

/// <summary>
/// The real web host over one migrated test database, with rate limits high enough for many test users, a fast outbox
/// poll, captured emails (<see cref="Emails"/>) and the Local blob provider (<see cref="ServiceWebFactory.BlobRootPath"/>).
/// </summary>
internal sealed class OrdersWebFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? overrides = null,
    Action<IServiceCollection>? configureServices = null) : ServiceWebFactory
{
    public string ConnectionString => connectionString;

    /// <summary>The emails sent by the host.</summary>
    public CapturingNotificationSender Emails => Services.GetRequiredService<CapturingNotificationSender>();

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
            new CapturingNotificationsRegistration().Register(services, new ConfigurationBuilder().Build());
            configureServices?.Invoke(services);
        });
    }
}
