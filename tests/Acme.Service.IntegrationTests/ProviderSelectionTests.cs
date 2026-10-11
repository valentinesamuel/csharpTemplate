using System.Diagnostics;
using System.Net;
using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.BuildingBlocks.Persistence.Locking;
using Acme.Service.Host.Providers;
using Acme.Service.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.IntegrationTests;

/// <summary>
/// Provider selection end to end (D-02, D-21, INV-17). Startup failures are asserted on the real Host process
/// (exit code and the logged message), because Program reports them itself and exits 1 before a
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> could observe a host.
/// </summary>
public sealed class ProviderSelectionTests
{
    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Start_UnknownCacheProvider_ExitsWith1NamingKey()
    {
        var result = await RunHostAsync("Testing", new Dictionary<string, string>
        {
            ["Cache__Provider"] = "Foo",
            ["Blob__Provider"] = "Local",
            ["Notifications__Provider"] = "Console",
            ["Jobs__Provider"] = "InMemory",
            ["Locks__Provider"] = "InMemory",
        });

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("Cache:Provider value 'Foo' does not match a registered provider");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Start_LocalBlobInProduction_ExitsWith1NamingKey()
    {
        var result = await RunHostAsync("Production", new Dictionary<string, string>
        {
            ["Blob__Provider"] = "Local",
        });

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("Blob:Provider=Local is a development-only provider and is not allowed in the Production environment");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Start_SmtpWithInvalidConfiguration_ExitsWith1NamingKeysWithoutEchoingPassword()
    {
        const string password = "provider-selection-smtp-secret-3e9";
        var result = await RunHostAsync("Production", new Dictionary<string, string>
        {
            ["Blob__Provider"] = "S3",
            ["Blob__S3__Bucket"] = "bucket",
            ["Blob__S3__Region"] = "eu-west-1",
            ["Notifications__Provider"] = "Smtp",
            ["Notifications__Smtp__Port"] = "not-a-port",
            ["Notifications__Smtp__Password"] = password,
            ["Jobs__Provider"] = "Hangfire",
            ["ConnectionStrings__Postgres"] = "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody",
        });

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("Notifications:Smtp:Host is required when Notifications:Provider is Smtp.");
        result.Output.ShouldContain("Notifications:Smtp:From is required when Notifications:Provider is Smtp.");
        result.Output.ShouldContain("Notifications:Smtp:Port must be an integer between 1 and 65535.");
        result.Output.ShouldNotContain(password);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Start_HangfireWithoutConnectionString_ExitsWith1NamingKey()
    {
        var result = await RunHostAsync("Production", new Dictionary<string, string>
        {
            ["Blob__Provider"] = "S3",
            ["Blob__S3__Bucket"] = "bucket",
            ["Blob__S3__Region"] = "eu-west-1",
            ["Notifications__Provider"] = "Smtp",
            ["Notifications__Smtp__Host"] = "smtp.example.com",
            ["Notifications__Smtp__From"] = "no-reply@example.com",
            ["Jobs__Provider"] = "Hangfire",
            ["Jobs__Hangfire__WorkerCount"] = "0",
        });

        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("ConnectionStrings:Postgres is required when Jobs:Provider is Hangfire.");
        result.Output.ShouldContain("Jobs:Hangfire:WorkerCount must be an integer between 1 and 1000.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Start_DefaultTestProviders_ResolvesInMemoryProvidersAndServesLive()
    {
        await using var factory = new ServiceWebFactory();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        var inMemory = typeof(InMemoryCacheRegistration).Assembly;
        factory.Services.GetRequiredService<ICache>().GetType().Assembly.ShouldBe(inMemory);
        factory.Services.GetRequiredService<IBlobStore>().GetType().Assembly.ShouldBe(inMemory);
        factory.Services.GetRequiredService<INotificationSender>().GetType().Assembly.ShouldBe(inMemory);
        factory.Services.GetRequiredService<IJobQueue>().GetType().Assembly.ShouldBe(inMemory);
        factory.Services.GetRequiredService<IDistributedLock>().GetType().Assembly.ShouldBe(inMemory);
        factory.Services.GetRequiredService<IOptionsMonitor<ProviderOptions>>().Get("Cache").Provider.ShouldBe("InMemory");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Start_PostgresLockSelected_ResolvesPostgresLock()
    {
        await using var factory = new ConfiguredWebFactory(new Dictionary<string, string?>
        {
            ["Locks:Provider"] = "Postgres",
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Timeout=2",
        });
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Services.GetRequiredService<IDistributedLock>().GetType().Assembly.ShouldBe(typeof(PostgresLockRegistration).Assembly);
    }

    private static async Task<(int ExitCode, string Output)> RunHostAsync(string environment, Dictionary<string, string> variables)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetHost(),
            WorkingDirectory = baseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.Combine(baseDirectory, "Acme.Service.Host.dll"));

        // A clean, explicit configuration: no inherited ASP.NET Core/.NET settings or provider keys.
        foreach (var key in startInfo.Environment.Keys.Where(IsInheritedSetting).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        startInfo.Environment["Blob__Local__RootPath"] = Path.Combine(Path.GetTempPath(), "service-tests", "provider-selection");
        foreach (var (key, value) in variables)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Host process.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(_processTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The Host did not exit within {_processTimeout}; it should fail at startup.");
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private static bool IsInheritedSetting(string key) =>
        key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
        || key.Contains("__", StringComparison.Ordinal);

    private static string DotnetHost()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrEmpty(hostPath) && File.Exists(hostPath) ? hostPath : "dotnet";
    }

    private sealed class ConfiguredWebFactory(IReadOnlyDictionary<string, string?> overrides) : ServiceWebFactory
    {
        protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides => overrides;
    }
}
