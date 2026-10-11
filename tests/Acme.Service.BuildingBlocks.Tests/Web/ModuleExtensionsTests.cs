using System.Net;
using Acme.Service.BuildingBlocks.Security;
using Acme.Service.BuildingBlocks.Tests.UseCases.Fixtures;
using Acme.Service.BuildingBlocks.Tests.Web.Fixtures;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Web;

public sealed class ModuleExtensionsTests
{
    [Fact]
    public void AddModules_Module_RunsRegistrationHooksAndRegistersRegistry()
    {
        var module = new SampleModule("things");
        var builder = WebApplication.CreateBuilder();

        builder.AddModules(module);

        module.Calls.ShouldBe(["AddPersistence", "AddServices"]);
        var registry = builder.Services.Single(d => d.ServiceType == typeof(ModuleRegistry)).ImplementationInstance
            .ShouldBeOfType<ModuleRegistry>();
        registry.Modules.ShouldBe([module]);
        registry.GetAssembly("things").ShouldBe(typeof(SampleModule).Assembly);
        registry.FindModuleName(typeof(SampleModule).Assembly).ShouldBe("things");
    }

    [Fact]
    public void AddModules_Module_MapsItsUseCasesToTheModuleKey()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddModules(new SampleModule("things"));

        var registry = builder.Services.Single(d => d.ServiceType == typeof(UseCaseModuleRegistry)).ImplementationInstance
            .ShouldBeOfType<UseCaseModuleRegistry>();
        registry.GetModuleKey(typeof(CreateThingCommand)).ShouldBe("things");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Orders")]
    [InlineData("1orders")]
    [InlineData("or-ders")]
    public void AddModules_InvalidName_Throws(string name)
    {
        var builder = WebApplication.CreateBuilder();

        Should.Throw<ArgumentException>(() => builder.AddModules(new SampleModule(name)));
    }

    [Fact]
    public void AddModules_DuplicateName_Throws()
    {
        var builder = WebApplication.CreateBuilder();

        Should.Throw<ArgumentException>(() => builder.AddModules(new SampleModule("things"), new SampleModule("things")))
            .Message.ShouldContain("more than once");
    }

    [Fact]
    public void AddModules_TwoModulesInOneAssembly_Throws()
    {
        var builder = WebApplication.CreateBuilder();

        Should.Throw<ArgumentException>(() => builder.AddModules(new SampleModule("things"), new SampleModule("others")))
            .Message.ShouldContain("one module per assembly");
    }

    [Fact]
    public void AddModules_CalledTwice_Throws()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddModules(new SampleModule("things"));

        Should.Throw<InvalidOperationException>(() => builder.AddModules());
    }

    [Fact]
    public async Task MapModuleEndpoints_WithoutAddModules_Throws()
    {
        await using var app = WebApplication.CreateBuilder().Build();

        Should.Throw<InvalidOperationException>(() => app.MapModuleEndpoints());
    }

    [Fact]
    public async Task MapModuleEndpoints_Module_MapsDiscoveredEndpointsAndModuleRoutes()
    {
        var module = new SampleModule("things");
        await using var app = await WebTestHost.StartAsync(
            builder => builder.AddModules(module),
            a => a.MapModuleEndpoints());
        using var client = app.GetTestClient();

        var discovered = await client.GetAsync(new Uri("/module-test/discovered", UriKind.Relative), TestContext.Current.CancellationToken);
        var moduleRoute = await client.GetAsync(new Uri("/module-test/module", UriKind.Relative), TestContext.Current.CancellationToken);

        discovered.StatusCode.ShouldBe(HttpStatusCode.OK);
        moduleRoute.StatusCode.ShouldBe(HttpStatusCode.OK);
        module.Calls.ShouldContain("MapEndpoints");
    }

    [Fact]
    public void FindEndpointTypes_Assembly_ReturnsOnlyConcreteEndpoints()
    {
        var types = EndpointDiscovery.FindEndpointTypes(typeof(SampleModule).Assembly);

        types.ShouldContain(typeof(DiscoveredEndpoint));
        types.ShouldNotContain(typeof(AbstractEndpoint));
    }

    private sealed class SampleModule(string name) : IModule
    {
        public List<string> Calls { get; } = [];

        public string Name => name;

        public IReadOnlyList<PermissionDefinition> Permissions { get; } =
            [new PermissionDefinition("things.read", "Read things.", ["Admin"])];

        public void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment) =>
            Calls.Add("AddServices");

        public void AddPersistence(IServiceCollection services, IConfiguration configuration) =>
            Calls.Add("AddPersistence");

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            Calls.Add("MapEndpoints");
            endpoints.MapGet("/module-test/module", () => TypedResults.Ok());
        }
    }
}

internal sealed class DiscoveredEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/module-test/discovered", () => TypedResults.Ok());
}

internal abstract class AbstractEndpoint : IEndpoint
{
    public abstract void Map(IEndpointRouteBuilder endpoints);
}
