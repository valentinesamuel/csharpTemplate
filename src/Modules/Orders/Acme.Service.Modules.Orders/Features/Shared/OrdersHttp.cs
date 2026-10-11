using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Acme.Service.BuildingBlocks.Web.Errors;
using Acme.Service.Modules.Orders.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Acme.Service.Modules.Orders.Features.Shared;

/// <summary>Routing, caller and authorization helpers shared by the order endpoints.</summary>
internal static class OrdersHttp
{
    /// <summary>Route prefix of every order endpoint (§0.5).</summary>
    public const string Prefix = "/api/v1/orders";

    /// <summary>Route of one order.</summary>
    public const string ItemPath = Prefix + "/{id:guid}";

    /// <summary>OpenAPI tag of the order endpoints.</summary>
    public const string Tag = "Orders";

    /// <summary>Claim carrying the user id (inbound claims are not mapped).</summary>
    public const string SubjectClaim = "sub";

    /// <summary>Claim carrying one granted permission (D-09).</summary>
    public const string PermissionClaim = "perm";

    /// <summary>The path of the order <paramref name="id"/>, for <c>Location</c>.</summary>
    public static string PathOf(Guid id) => $"{Prefix}/{id}";

    /// <summary>
    /// Requires an authenticated user holding at least one of <paramref name="permissions"/> (403 otherwise). For the
    /// endpoints open to owners with one permission or to anyone with <see cref="OrdersPermissions.Manage"/>; ownership is
    /// checked by the use case. Stateless: reads claims only (INV-13).
    /// </summary>
    public static RouteHandlerBuilder RequireAnyPermission(this RouteHandlerBuilder builder, params string[] permissions) =>
        builder.RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireClaim(PermissionClaim, permissions));

    /// <summary>Common OpenAPI metadata of the order endpoints.</summary>
    public static RouteHandlerBuilder WithOrdersMetadata(this RouteHandlerBuilder builder, string name, string summary) =>
        builder
            .WithName(name)
            .WithSummary(summary)
            .WithTags(Tag)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>401 for an authenticated caller whose token lacks the <c>sub</c> every access token carries.</summary>
    public static IResult MissingClaims() => TypedResults.Problem(
        detail: "The access token does not identify a user.",
        statusCode: StatusCodes.Status401Unauthorized,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { [ProblemDetailsSetup.CodeKey] = "unauthorized" });
}

/// <summary>The authenticated caller as the order use cases see it.</summary>
/// <param name="UserId">The user id (<c>sub</c>).</param>
/// <param name="CanManage">True when the caller holds <see cref="OrdersPermissions.Manage"/> (every order).</param>
internal sealed record OrdersCaller(Guid UserId, bool CanManage)
{
    /// <summary>Reads the caller from the access token claims; false when the token has no usable <c>sub</c>.</summary>
    public static bool TryFrom(ClaimsPrincipal user, [NotNullWhen(true)] out OrdersCaller? caller)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!Guid.TryParse(user.FindFirst(OrdersHttp.SubjectClaim)?.Value, out var userId) || userId == Guid.Empty)
        {
            caller = null;
            return false;
        }

        var canManage = user.HasClaim(c =>
            string.Equals(c.Type, OrdersHttp.PermissionClaim, StringComparison.Ordinal)
            && string.Equals(c.Value, OrdersPermissions.Manage, StringComparison.Ordinal));
        caller = new OrdersCaller(userId, canManage);
        return true;
    }

    /// <summary>True when the caller may act on an order of <paramref name="customerId"/>: its owner, or a manager.</summary>
    public bool MayAccess(Guid customerId) => CanManage || customerId == UserId;
}
