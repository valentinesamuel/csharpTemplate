using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Web.Authorization;

/// <summary>
/// Builds <c>perm:&lt;name&gt;</c> policies on demand (authenticated user plus <see cref="PermissionRequirement"/>).
/// Every other policy name, and the default and fallback policies, come from the default provider, so
/// policies configured in <see cref="AuthorizationOptions"/> (including <c>FallbackPolicy</c>, INV-12) keep working.
/// </summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);

        if (policyName.StartsWith(PermissionRequirement.PolicyPrefix, StringComparison.Ordinal)
            && policyName.Length > PermissionRequirement.PolicyPrefix.Length)
        {
            var permission = policyName[PermissionRequirement.PolicyPrefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}
