using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.BuildingBlocks.Web.Endpoints;

/// <summary>Finds and maps the <see cref="IEndpoint"/> implementations of an assembly.</summary>
internal static class EndpointDiscovery
{
    /// <summary>Concrete, non-generic <see cref="IEndpoint"/> types in <paramref name="assembly"/>, ordered by full name.</summary>
    public static IReadOnlyList<Type> FindEndpointTypes(Assembly assembly) =>
        [.. assembly.DefinedTypes
            .Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && typeof(IEndpoint).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => t.AsType())];

    /// <summary>Instantiates and maps every endpoint found in <paramref name="assembly"/>.</summary>
    public static void MapEndpoints(IEndpointRouteBuilder endpoints, Assembly assembly)
    {
        foreach (var type in FindEndpointTypes(assembly))
        {
            var endpoint = (IEndpoint)ActivatorUtilities.CreateInstance(endpoints.ServiceProvider, type);
            endpoint.Map(endpoints);
        }
    }
}
