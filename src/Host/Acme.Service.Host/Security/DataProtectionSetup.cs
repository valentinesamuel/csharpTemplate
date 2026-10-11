using Microsoft.AspNetCore.DataProtection;

namespace Acme.Service.Host.Security;

/// <summary>
/// Data Protection for every host of this service (D-27). The application name isolates the key ring and payloads
/// from other applications; the Auth module persists the keys to <c>auth.data_protection_keys</c>, so all replicas
/// (and the command hosts) share one key ring. Encrypting the key ring at rest is a documented hardening step (R-07).
/// </summary>
internal static class DataProtectionSetup
{
    /// <summary>The Data Protection application name (template token).</summary>
    public const string ApplicationName = "acme-service";

    public static IServiceCollection AddDataProtectionSetup(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName(ApplicationName);
        return services;
    }
}
