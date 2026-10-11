using System.Net;
using Acme.Service.Testing;
using Acme.Service.Testing.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>
/// The real web host over one migrated test database, for the account endpoints: emails are captured
/// (<see cref="CapturingNotificationsRegistration"/>), the outbox polls every 100 ms, and the rate limits are high
/// unless <paramref name="overrides"/> set them. A request may choose its client IP with <see cref="ClientIpHeader"/>
/// (TestServer has no remote address), applied before the forwarded headers middleware.
/// </summary>
internal sealed class AccountWebFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? overrides = null,
    Action<IServiceCollection>? configureServices = null) : ServiceWebFactory
{
    /// <summary>Test-only request header carrying the client IP address to simulate.</summary>
    public const string ClientIpHeader = "X-Test-Client-IP";

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
            services.AddTransient<IStartupFilter, ClientIpStartupFilter>();
            configureServices?.Invoke(services);
        });
    }

    private sealed class ClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[ClientIpHeader].ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
