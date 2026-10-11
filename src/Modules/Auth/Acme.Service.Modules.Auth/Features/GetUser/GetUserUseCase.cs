using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.Modules.Auth.Features.GetUser;

/// <summary>Reads one account with its roles and linked external providers; 404 <c>user_not_found</c> when unknown.</summary>
internal sealed class GetUserUseCase(AuthDbContext context, TimeProvider timeProvider) : IUseCase<GetUserQuery, UserResponse>
{
    public async Task<Result<UserResponse>> ExecuteAsync(GetUserQuery input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == input.UserId)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.EmailConfirmed, u.TwoFactorEnabled, u.LockoutEnd, u.CreatedAt })
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

        var logins = await context.Set<IdentityUserLogin<Guid>>().AsNoTracking()
            .Where(l => l.UserId == user.Id)
            .Select(l => l.LoginProvider)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var lockedOut = user.LockoutEnd is { } end && end > timeProvider.GetUtcNow();
        return new UserResponse(
            user.Id, user.Email, user.DisplayName, user.EmailConfirmed, user.TwoFactorEnabled, lockedOut, user.LockoutEnd, user.CreatedAt, roles, logins);
    }
}
