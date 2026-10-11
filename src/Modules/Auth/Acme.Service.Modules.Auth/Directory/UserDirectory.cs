using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.Modules.Auth.Directory;

/// <summary>Read-only user lookup for other modules. A locked-out user is reported as inactive.</summary>
internal sealed class UserDirectory(AuthDbContext context, TimeProvider timeProvider) : IUserDirectory
{
    public async Task<UserSummary?> GetUserSummaryAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.LockoutEnd })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            return null;
        }

        var isActive = user.LockoutEnd is null || user.LockoutEnd <= timeProvider.GetUtcNow();
        return new UserSummary(user.Id, user.Email ?? string.Empty, user.DisplayName, isActive);
    }
}
