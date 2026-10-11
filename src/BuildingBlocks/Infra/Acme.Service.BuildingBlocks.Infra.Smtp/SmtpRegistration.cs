using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.Smtp;

/// <summary>
/// <c>Notifications:Provider=Smtp</c>: an <see cref="INotificationSender"/> over SMTP (MailKit). Requires
/// <c>Notifications:Smtp:Host</c> and <c>Notifications:Smtp:From</c>; optional <c>Notifications:Smtp:Port</c> (default
/// 587), <c>Notifications:Smtp:UseStartTls</c> (default true) and <c>Notifications:Smtp:Username</c> +
/// <c>Notifications:Smtp:Password</c> (together, STARTTLS only). Options are validated at startup; startup never
/// contacts the server.
/// </summary>
public sealed class SmtpRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "Smtp";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Notifications;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SmtpOptions>()
            .Configure(o => SmtpOptions.Read(configuration, o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SmtpOptions>, SmtpOptions.Validator>());

        services.AddSingleton<INotificationSender, SmtpNotificationSender>();
    }
}
