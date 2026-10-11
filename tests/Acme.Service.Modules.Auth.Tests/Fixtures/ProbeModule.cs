using Acme.Service.BuildingBlocks.Security;
using Acme.Service.BuildingBlocks.Web.Authorization;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>
/// A test-only module whose endpoints exercise the Host's authentication and authorization (the real account endpoints
/// arrive in P13). Registered into the real Host by <see cref="ProbeWebFactory"/>.
/// </summary>
internal sealed class ProbeModule : IModule
{
    /// <summary>Requires an authenticated caller; echoes the caller's claims.</summary>
    public const string AuthenticatedPath = "/probe/authenticated";

    /// <summary>No authorization metadata at all: protected only by the fallback policy.</summary>
    public const string FallbackPath = "/probe/fallback";

    /// <summary>Requires the <c>users.read</c> permission claim.</summary>
    public const string PermissionPath = "/probe/users-read";

    /// <summary>Requires the <c>live-session</c> policy.</summary>
    public const string LiveSessionPath = "/probe/live";

    public string Name => "probe";

    public IReadOnlyList<PermissionDefinition> Permissions { get; } = [];

    public void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
    }

    public void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AuthenticatedPath, (HttpContext context) => TypedResults.Ok(new ProbeClaims(
                context.User.FindFirst("sub")?.Value,
                context.User.FindFirst("sid")?.Value,
                context.User.FindFirst("email")?.Value,
                [.. context.User.FindAll("role").Select(c => c.Value)],
                [.. context.User.FindAll("perm").Select(c => c.Value)],
                [.. context.User.FindAll("amr").Select(c => c.Value)])))
            .RequireAuthorization();

        endpoints.MapGet(FallbackPath, () => TypedResults.Ok("fallback"));

        endpoints.MapGet(PermissionPath, () => TypedResults.Ok("users.read"))
            .RequirePermission(AuthPermissions.UsersRead);

        endpoints.MapGet(LiveSessionPath, () => TypedResults.Ok("live"))
            .RequireAuthorization(LiveSessionRequirement.PolicyName);
    }
}

/// <summary>The claims echoed by <see cref="ProbeModule.AuthenticatedPath"/>.</summary>
internal sealed record ProbeClaims(string? Sub, string? Sid, string? Email, string[] Roles, string[] Perms, string[] Amr);
