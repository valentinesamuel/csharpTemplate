using Microsoft.AspNetCore.Authorization;

namespace Acme.Service.BuildingBlocks.Web.Authorization;

/// <summary>Requires the user to hold the <c>perm</c> claim <paramref name="Permission"/>.</summary>
/// <param name="Permission">Lower-case dotted permission name, for example <c>orders.read</c>.</param>
internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement
{
    /// <summary>Claim type carrying one granted permission per claim (issued by Auth, P12).</summary>
    public const string ClaimType = "perm";

    /// <summary>Prefix of the dynamic permission policy names.</summary>
    public const string PolicyPrefix = "perm:";
}
