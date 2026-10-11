using Acme.Service.Modules.Auth.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Auth.Options;

/// <summary>
/// Top-level <c>Auth</c> settings (§0.7) that belong to no sub-section: the role given to self-registered users
/// (§0.8). Validated at startup (INV-17).
/// </summary>
internal sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The role assigned to every self-registered user; one of the seed roles.</summary>
    public string DefaultRole { get; set; } = AuthRole.Customer;

    /// <summary>Registers the options with key-named validation and <c>ValidateOnStart</c>.</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(
                o => AuthRole.SeedRoles.Contains(o.DefaultRole, StringComparer.Ordinal),
                $"{SectionName}:{nameof(DefaultRole)} must be one of the seed roles: {string.Join(", ", AuthRole.SeedRoles)}.")
            .ValidateOnStart();
    }
}
