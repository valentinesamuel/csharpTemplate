using Acme.Service.BuildingBlocks.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Acme.Service.Host.Health;

/// <summary>
/// <c>/health/live</c> (process only, tag <c>live</c>) and <c>/health/ready</c> (dependencies, tag
/// <c>ready</c>: Postgres connectivity and no pending migrations, plus Degraded-only checks added by later
/// phases). Both are anonymous and excluded from OpenAPI (D-20).
/// </summary>
internal static class HealthEndpoints
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    public static IServiceCollection AddHealthEndpoints(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag])
            .AddPersistenceHealthChecks(ReadyTag);
        return services;
    }

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(LiveTag) })
            .AllowAnonymous()
            .ExcludeFromDescription();

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) })
            .AllowAnonymous()
            .ExcludeFromDescription();

        return endpoints;
    }
}
