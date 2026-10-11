using Acme.Service.BuildingBlocks.UseCases;

namespace Acme.Service.Modules.Auth.Features.Me;

/// <summary>The caller's own account.</summary>
/// <param name="UserId">The caller (<c>sub</c>).</param>
internal sealed record MeQuery(Guid UserId) : IQuery<MeResponse>;

/// <summary>The body of <c>GET /api/v1/auth/me</c>: the account as stored now (roles and permissions are current, not the token's).</summary>
internal sealed record MeResponse(
    Guid Id,
    string? Email,
    string? DisplayName,
    bool EmailConfirmed,
    bool MfaEnabled,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
