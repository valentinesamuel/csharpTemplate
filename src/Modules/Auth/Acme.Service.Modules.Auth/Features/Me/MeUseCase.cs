using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.Modules.Auth.Features.Me;

/// <summary>
/// Reads the caller's account, roles and the permissions those roles grant, as stored now. A user deleted since the
/// token was issued is 404 <c>user_not_found</c>.
/// </summary>
internal sealed class MeUseCase(AuthDbContext context) : IUseCase<MeQuery, MeResponse>
{
    public async Task<Result<MeResponse>> ExecuteAsync(MeQuery input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == input.UserId)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.EmailConfirmed, u.TwoFactorEnabled })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return RefreshTokenService.UserNotFound;
        }

        var roles = await (
                from userRole in context.UserRoles
                join role in context.Roles on userRole.RoleId equals role.Id
                where userRole.UserId == user.Id && role.Name != null
                orderby role.Name
                select role.Name!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var permissions = await (
                from userRole in context.UserRoles
                join grant in context.RolePermissions on userRole.RoleId equals grant.RoleId
                where userRole.UserId == user.Id
                select grant.PermissionName)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new MeResponse(user.Id, user.Email, user.DisplayName, user.EmailConfirmed, user.TwoFactorEnabled, roles, permissions);
    }
}
