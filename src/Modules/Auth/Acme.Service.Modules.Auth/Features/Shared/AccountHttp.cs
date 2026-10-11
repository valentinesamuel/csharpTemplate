using System.Security.Claims;
using Acme.Service.BuildingBlocks.Web.Errors;
using Acme.Service.Modules.Auth.Sessions;
using Acme.Service.Modules.Auth.Tokens;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Acme.Service.Modules.Auth.Features.Shared;

/// <summary>Routing and request helpers shared by the account endpoints.</summary>
internal static class AccountHttp
{
    /// <summary>Route prefix of every account endpoint (§0.5).</summary>
    public const string Prefix = "/api/v1/auth";

    /// <summary>OpenAPI tag of the account endpoints.</summary>
    public const string Tag = "Auth";

    /// <summary>The client address and user agent recorded with a refresh token. Informational only.</summary>
    public static ClientInfo ClientOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userAgent = context.Request.Headers[HeaderNames.UserAgent].ToString();
        return new ClientInfo(ClientAddress(context), userAgent.Length == 0 ? null : userAgent);
    }

    /// <summary>
    /// The caller's IP address as a string (IPv4-mapped IPv6 addresses as IPv4), or null when unknown. The forwarded
    /// headers middleware has already replaced it with the client address when the request came through a trusted proxy.
    /// </summary>
    public static string? ClientAddress(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    /// <summary>The <c>sub</c> of an authenticated caller; false when the token carries none (never for our tokens).</summary>
    public static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Guid.TryParse(user.FindFirst(AuthClaimTypes.Subject)?.Value, out userId);
    }

    /// <summary>The <c>sid</c> (session id) of an authenticated caller; false when the token carries none.</summary>
    public static bool TryGetSessionId(ClaimsPrincipal user, out Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Guid.TryParse(user.FindFirst(AuthClaimTypes.SessionId)?.Value, out sessionId);
    }

    /// <summary>401 for an authenticated caller whose token lacks the claims every access token carries.</summary>
    public static IResult MissingClaims() => TypedResults.Problem(
        detail: "The access token does not identify a user.",
        statusCode: StatusCodes.Status401Unauthorized,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { [ProblemDetailsSetup.CodeKey] = "unauthorized" });
}
