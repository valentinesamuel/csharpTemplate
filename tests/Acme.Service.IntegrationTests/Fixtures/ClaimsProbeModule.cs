using Acme.Service.BuildingBlocks.Security;
using Acme.Service.BuildingBlocks.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.IntegrationTests.Fixtures;

/// <summary>
/// A test-only module mapping <c>GET /test/claims</c>: requires an authenticated caller and echoes the caller's claims,
/// reading nothing else (no database, no cache). It is registered only by <see cref="IntegrationWebFactory"/>, never by
/// the Host, so it shows whether stateless token validation works while the Auth database is down (INV-13).
/// </summary>
internal sealed class ClaimsProbeModule : IModule
{
    /// <summary>Path of the claims endpoint.</summary>
    public const string ClaimsPath = "/test/claims";

    public string Name => "test_probe";

    public IReadOnlyList<PermissionDefinition> Permissions { get; } = [];

    public void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
    }

    public void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(ClaimsPath, (HttpContext context) => TypedResults.Ok(new ProbeClaims(
                context.User.FindFirst("sub")?.Value,
                context.User.FindFirst("sid")?.Value,
                [.. context.User.FindAll("role").Select(c => c.Value)],
                [.. context.User.FindAll("perm").Select(c => c.Value)])))
            .RequireAuthorization();
}

/// <summary>The claims echoed by <see cref="ClaimsProbeModule.ClaimsPath"/>.</summary>
internal sealed record ProbeClaims(string? Sub, string? Sid, string[] Roles, string[] Perms);
