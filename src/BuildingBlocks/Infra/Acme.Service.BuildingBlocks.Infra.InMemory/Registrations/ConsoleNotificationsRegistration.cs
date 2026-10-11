using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;

/// <summary>
/// <c>Notifications:Provider=Console</c>: an <see cref="INotificationSender"/> that only logs (bodies in
/// Development only). Development only (D-21).
/// </summary>
public sealed class ConsoleNotificationsRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "Console";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Notifications;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<INotificationSender, ConsoleNotificationSender>();
    }
}
