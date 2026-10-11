using Acme.Service.Testing.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Acme.Service.Testing;

/// <summary>
/// Base <see cref="WebApplicationFactory{TEntryPoint}"/> for the Host. Runs in the <c>Testing</c>
/// environment with the dev/test providers selected (<see cref="ProviderDefaults"/>), a runtime-generated JWT signing
/// key (<see cref="SecurityDefaults"/>), placeholder application settings (<see cref="ApplicationDefaults"/>), and
/// layers <see cref="ConfigurationOverrides"/> on top of the app configuration.
/// Derive and override <see cref="ConfigurationOverrides"/> to supply settings such as connection strings.
/// </summary>
/// <remarks>
/// Defaults and overrides are applied with <c>UseSetting</c> (visible to code that reads configuration before
/// <c>Build()</c>, such as the provider registry) and again as an in-memory collection (highest precedence).
/// </remarks>
public class ServiceWebFactory : WebApplicationFactory<Program>
{
    /// <summary>The hosting environment used by the factory.</summary>
    public const string EnvironmentName = "Testing";

    /// <summary>Blob root of this factory's local blob provider (a fresh temporary directory).</summary>
    public string BlobRootPath { get; } = Path.Combine(Path.GetTempPath(), "service-tests", "blobs", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Provider selection for tests (D-02): every category on its in-process provider, so the host starts without
    /// external infrastructure. <see cref="ConfigurationOverrides"/> wins over these.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string?> ProviderDefaults => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Cache:Provider"] = "InMemory",
        ["Blob:Provider"] = "Local",
        ["Blob:Local:RootPath"] = BlobRootPath,
        ["Notifications:Provider"] = "Console",
        ["Jobs:Provider"] = "InMemory",
        ["Locks:Provider"] = "InMemory",
    };

    /// <summary>
    /// The JWT key ring for tests: one <c>Active</c> key generated at runtime (<see cref="TestSigningKeys"/>; never a
    /// committed key, INV-11). <see cref="ConfigurationOverrides"/> wins over these.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string?> SecurityDefaults =>
        new Dictionary<string, string?>(
            TestSigningKeys.Entry(0, TestSigningKeys.DefaultKid, "Active", TestSigningKeys.DefaultPrivateKeyBase64),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Required application settings that have no production default (the client page of emailed links). Placeholder
    /// values on a reserved domain; <see cref="ConfigurationOverrides"/> wins over these.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string?> ApplicationDefaults => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Auth:Links:ConfirmEmailUrl"] = "https://app.example.test/confirm-email",
        ["Auth:Links:ResetPasswordUrl"] = "https://app.example.test/reset-password",
    };

    /// <summary>Configuration values applied last (highest precedence). Keys use the <c>:</c> separator.</summary>
    protected virtual IReadOnlyDictionary<string, string?> ConfigurationOverrides { get; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(EnvironmentName);

        var settings = new Dictionary<string, string?>(ProviderDefaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in SecurityDefaults)
        {
            settings[key] = value;
        }

        foreach (var (key, value) in ApplicationDefaults)
        {
            settings[key] = value;
        }

        foreach (var (key, value) in ConfigurationOverrides)
        {
            settings[key] = value;
        }

        foreach (var (key, value) in settings)
        {
            if (value is not null)
            {
                builder.UseSetting(key, value);
            }
        }

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(BlobRootPath))
        {
            Directory.Delete(BlobRootPath, recursive: true);
        }
    }
}
