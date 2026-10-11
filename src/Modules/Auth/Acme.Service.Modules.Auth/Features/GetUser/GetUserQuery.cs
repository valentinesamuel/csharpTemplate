using Acme.Service.BuildingBlocks.UseCases;

namespace Acme.Service.Modules.Auth.Features.GetUser;

/// <summary>One user account, for administrators.</summary>
/// <param name="UserId">The user.</param>
internal sealed record GetUserQuery(Guid UserId) : IQuery<UserResponse>;

/// <summary>The body of <c>GET /api/v1/auth/users/{id}</c>: the account as stored now.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Email">The email address.</param>
/// <param name="DisplayName">The display name, if any.</param>
/// <param name="EmailConfirmed">Whether the email address is confirmed.</param>
/// <param name="MfaEnabled">Whether two-factor authentication is on.</param>
/// <param name="LockedOut">Whether the account is locked now.</param>
/// <param name="LockoutEnd">When the lock ends (UTC), if the account is or was locked.</param>
/// <param name="CreatedAt">When the account was created (UTC).</param>
/// <param name="Roles">The user's roles.</param>
/// <param name="ExternalLogins">The external login providers linked to the account (provider names only).</param>
internal sealed record UserResponse(
    Guid Id,
    string? Email,
    string? DisplayName,
    bool EmailConfirmed,
    bool MfaEnabled,
    bool LockedOut,
    DateTimeOffset? LockoutEnd,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> ExternalLogins);
