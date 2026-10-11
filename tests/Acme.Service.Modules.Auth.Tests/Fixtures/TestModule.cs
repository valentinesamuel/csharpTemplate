using Acme.Service.BuildingBlocks.Security;
using Acme.Service.BuildingBlocks.Web.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>A second module that only declares permissions, to exercise the cross-module catalog.</summary>
internal sealed class TestModule(params PermissionDefinition[] permissions) : IModule
{
    public string Name => "widgets";

    public IReadOnlyList<PermissionDefinition> Permissions { get; } = permissions;

    public void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
    }

    public void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
