using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Modules;
using Acme.Service.Host.Security;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Login;
using Acme.Service.Modules.Auth.Features.LoginMfa;
using Acme.Service.Modules.Auth.Features.MfaVerify;
using Acme.Service.Modules.Auth.Features.Register;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Notifications;
using Acme.Service.Testing.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Unit;

public sealed class AuthServiceRegistrationTests
{
    [Fact]
    public void AddPersistenceAndServices_ValidateOnBuild_ResolvesIdentityDirectoryUseCasesAndJobs()
    {
        // Development builds the provider with ValidateOnBuild; every Auth registration must be constructible.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=none",
                ["Auth:Jwt:Issuer"] = "issuer",
                ["Auth:Jwt:Audience"] = "audience",
                ["Auth:Links:ConfirmEmailUrl"] = "https://app.example.test/confirm-email",
                ["Auth:Links:ResetPasswordUrl"] = "https://app.example.test/reset-password",
                ["Auth:Mfa:Issuer"] = "Test Service",
            })
            .AddInMemoryCollection(TestSigningKeys.Entry(0, TestSigningKeys.DefaultKid, "Active", TestSigningKeys.DefaultPrivateKeyBase64))
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddRouting(); // Provided by the web host; the authorization policy cache needs EndpointDataSource.
        services.AddDataProtectionSetup();

        // Provided by the Host's provider registry (D-02); the email flow's handler and job depend on them.
        services.AddSingleton(Substitute.For<IJobQueue>());
        services.AddSingleton(Substitute.For<INotificationSender>());
        // <sample:orders>
        // The Orders attachments and expiry job depend on these.
        services.AddSingleton(Substitute.For<Acme.Service.BuildingBlocks.Blob.IBlobStore>());
        services.AddSingleton(Substitute.For<Acme.Service.BuildingBlocks.Locking.IDistributedLock>());
        // </sample:orders>
        var modules = HostModules.Create();
        services.AddModuleRegistry(modules);
        foreach (var module in modules)
        {
            services.AddUseCasesFromAssembly(module.GetType().Assembly, module.Name);
            module.AddPersistence(services, configuration);
            module.AddServices(services, configuration, Substitute.For<IHostEnvironment>());
        }

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>().SupportsUserAuthenticatorKey.ShouldBeTrue();
        scope.ServiceProvider.GetRequiredService<RoleManager<AuthRole>>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<SignInManager<AuthUser>>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IUserStore<AuthUser>>().GetType().Name.ShouldBe("AuthUserStore");
        scope.ServiceProvider.GetRequiredService<IUserDirectory>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IUseCaseExecutor<RegisterCommand, Done>>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IUseCaseExecutor<LoginCommand, LoginResponse>>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<SendEmailVerificationJob>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<SendPasswordResetJob>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IUseCaseExecutor<LoginMfaCommand, TokenResponse>>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IUseCaseExecutor<MfaVerifyCommand, RecoveryCodesResponse>>().ShouldNotBeNull();
    }
}
