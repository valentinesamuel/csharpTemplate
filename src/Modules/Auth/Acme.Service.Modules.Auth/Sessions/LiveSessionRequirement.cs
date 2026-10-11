using Microsoft.AspNetCore.Authorization;

namespace Acme.Service.Modules.Auth.Sessions;

/// <summary>
/// Requires the session named by the token's <c>sid</c> claim to be live: present in <c>auth.sessions</c>, owned by
/// <c>sub</c>, not revoked and not past its absolute expiry (D-29). Opt-in per endpoint through the
/// <see cref="PolicyName"/> policy; never part of token validation (INV-13).
/// </summary>
internal sealed class LiveSessionRequirement : IAuthorizationRequirement
{
    /// <summary>The policy name endpoints opt in with: <c>.RequireAuthorization("live-session")</c>.</summary>
    public const string PolicyName = "live-session";
}

/// <summary>Why a <see cref="LiveSessionRequirement"/> failed.</summary>
internal sealed class LiveSessionFailureReason(IAuthorizationHandler handler, bool storeUnavailable, string message)
    : AuthorizationFailureReason(handler, message)
{
    /// <summary>
    /// True when the session lookup could not complete (Auth database unavailable, timeout): the request is denied
    /// with 503 <c>session_store_unavailable</c>. False when the session is not live: 401 <c>session_revoked</c>.
    /// </summary>
    public bool StoreUnavailable { get; } = storeUnavailable;
}
