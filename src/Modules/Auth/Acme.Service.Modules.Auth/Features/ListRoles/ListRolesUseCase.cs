using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.Modules.Auth.Features.ListRoles;

/// <summary>Reads the roles and their permission grants (two queries, ordered by name).</summary>
internal sealed class ListRolesUseCase(AuthDbContext context) : IUseCase<ListRolesQuery, IReadOnlyList<RoleResponse>>
{
    public async Task<Result<IReadOnlyList<RoleResponse>>> ExecuteAsync(ListRolesQuery input, CancellationToken cancellationToken)
    {
        var roles = await context.Roles.AsNoTracking()
            .Where(r => r.Name != null)
            .OrderBy(r => r.Name)
            .Select(r => new { r.Id, Name = r.Name! })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var grants = await context.RolePermissions.AsNoTracking()
            .OrderBy(g => g.PermissionName)
            .Select(g => new { g.RoleId, g.PermissionName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byRole = grants.ToLookup(g => g.RoleId, g => g.PermissionName);

        return roles.Select(r => new RoleResponse(r.Name, [.. byRole[r.Id]])).ToList();
    }
}
