using Microsoft.AspNetCore.Routing;

namespace Acme.Service.BuildingBlocks.Web.Endpoints;

/// <summary>
/// One HTTP endpoint of a vertical slice (plan §0.5). Implementations are concrete, non-generic classes
/// in a module assembly; they are discovered and mapped by <see cref="Modules.ModuleExtensions.MapModuleEndpoints"/>.
/// Constructor dependencies, if any, are resolved from the root service provider at startup.
/// </summary>
public interface IEndpoint
{
    /// <summary>Maps the endpoint's route(s).</summary>
    void Map(IEndpointRouteBuilder endpoints);
}
