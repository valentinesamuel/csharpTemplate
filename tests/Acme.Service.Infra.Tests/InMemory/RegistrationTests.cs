using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Infra.InMemory;
using Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Acme.Service.Infra.Tests.InMemory;

public sealed class RegistrationTests
{
    public static TheoryData<Type, ProviderCategory, string, Type> Registrations => new()
    {
        { typeof(InMemoryCacheRegistration), ProviderCategory.Cache, "InMemory", typeof(ICache) },
        { typeof(LocalBlobRegistration), ProviderCategory.Blob, "Local", typeof(IBlobStore) },
        { typeof(ConsoleNotificationsRegistration), ProviderCategory.Notifications, "Console", typeof(INotificationSender) },
        { typeof(InMemoryJobsRegistration), ProviderCategory.Jobs, "InMemory", typeof(IJobQueue) },
        { typeof(InMemoryLockRegistration), ProviderCategory.Locks, "InMemory", typeof(IDistributedLock) },
    };

    [Theory]
    [MemberData(nameof(Registrations))]
    public void Registration_ValidConfiguration_DeclaresCategoryAndNameAndResolvesPort(Type registrationType, ProviderCategory category, string name, Type port)
    {
        var registration = (IProviderRegistration)Activator.CreateInstance(registrationType)!;
        using var root = new TempDirectory();
        using var provider = Build(registration, new Dictionary<string, string?> { ["Blob:Local:RootPath"] = root.Path });

        registration.Category.ShouldBe(category);
        registration.Name.ShouldBe(name);
        provider.GetRequiredService(port).GetType().Assembly.ShouldBe(typeof(InMemoryCacheRegistration).Assembly);
    }

    [Fact]
    public void LocalBlobRegistration_RelativeRoot_ResolvesAgainstContentRoot()
    {
        using var contentRoot = new TempDirectory();
        using var provider = Build(new LocalBlobRegistration(), new Dictionary<string, string?> { ["Blob:Local:RootPath"] = ".data/blobs" }, contentRoot.Path);

        var store = (LocalDiskBlobStore)provider.GetRequiredService<IBlobStore>();

        store.RootPath.ShouldBe(Path.Combine(contentRoot.Path, ".data", "blobs"));
    }

    [Fact]
    public void LocalBlobRegistration_MissingRoot_ValidationNamesKey()
    {
        using var provider = Build(new LocalBlobRegistration(), []);

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<LocalBlobOptions>>().Value);

        exception.Message.ShouldContain("Blob:Local:RootPath");
    }

    private static ServiceProvider Build(IProviderRegistration registration, Dictionary<string, string?> settings, string? contentRoot = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        environment.ContentRootPath.Returns(contentRoot ?? Path.GetTempPath());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(environment);
        registration.Register(services, configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "service-tests", "registration", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
