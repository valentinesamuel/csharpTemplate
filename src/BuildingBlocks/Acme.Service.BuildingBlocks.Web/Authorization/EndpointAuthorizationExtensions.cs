using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.BuildingBlocks.Web.Authorization;

/// <summary>Permission-based authorization for endpoints (plan §0.5, §0.8).</summary>
public static class EndpointAuthorizationExtensions
{
    /// <summary>
    /// Registers authorization with the dynamic <c>perm:&lt;name&gt;</c> policy provider and the permission handler.
    /// Other policies and <see cref="AuthorizationOptions"/> settings (for example the fallback policy) are unaffected.
    /// </summary>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization();
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, PermissionAuthorizationHandler>());
        return services;
    }

    /// <summary>
    /// Requires an authenticated user holding the <c>perm</c> claim <paramref name="permission"/>.
    /// Requires <see cref="AddPermissionAuthorization"/>.
    /// </summary>
    /// <param name="builder">Endpoint or group builder.</param>
    /// <param name="permission">Lower-case dotted <c>&lt;resource&gt;.&lt;action&gt;</c>, for example <c>orders.read</c>.</param>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!IsValidPermission(permission))
        {
            throw new ArgumentException(
                $"Permission '{permission}' is invalid. Use lower-case dotted '<resource>.<action>' names.", nameof(permission));
        }

        return builder.RequireAuthorization(PermissionRequirement.PolicyPrefix + permission);
    }

    internal static bool IsValidPermission(string? permission)
    {
        if (string.IsNullOrEmpty(permission))
        {
            return false;
        }

        var segments = permission.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || !char.IsAsciiLetterLower(segment[0]))
            {
                return false;
            }

            foreach (var c in segment)
            {
                if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-' && c != '_')
                {
                    return false;
                }
            }
        }

        return true;
    }
}
