using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Sessions;

/// <summary>
/// Evaluates <see cref="LiveSessionRequirement"/> with one read of <c>auth.sessions</c> (never Redis or the cache).
/// Fails closed (D-29, INV-13): when the lookup cannot complete, the requirement fails with a
/// <see cref="LiveSessionFailureReason"/> marked <see cref="LiveSessionFailureReason.StoreUnavailable"/>, which
/// <see cref="LiveSessionResultHandler"/> turns into 503 <c>session_store_unavailable</c>. It never succeeds on error.
/// </summary>
/// <remarks>
/// The context is resolved from the request scope only when the requirement is evaluated: the authorization middleware
/// constructs every handler on every request, and an unusable database configuration must deny (503) only the
/// endpoints that opt in, never break the others.
/// </remarks>
internal sealed partial class LiveSessionHandler(IServiceProvider services, TimeProvider timeProvider, ILogger<LiveSessionHandler> logger)
    : AuthorizationHandler<LiveSessionRequirement>
{
    /// <summary>Upper bound of the session lookup; slower lookups fail closed.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, LiveSessionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Anonymous callers are rejected by the policy's authenticated-user requirement (401 challenge).
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (!Guid.TryParse(context.User.FindFirst(AuthClaimTypes.SessionId)?.Value, out var sessionId)
            || !Guid.TryParse(context.User.FindFirst(AuthClaimTypes.Subject)?.Value, out var userId))
        {
            context.Fail(new LiveSessionFailureReason(this, storeUnavailable: false, "The token carries no session."));
            return;
        }

        var requestAborted = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(LookupTimeout);

        bool live;
        try
        {
            var dbContext = services.GetRequiredService<AuthDbContext>();
            var session = await dbContext.Sessions.AsNoTracking()
                .Where(s => s.Id == sessionId && s.UserId == userId)
                .Select(s => new { s.RevokedAt, s.AbsoluteExpiresAt })
                .SingleOrDefaultAsync(timeout.Token)
                .ConfigureAwait(false);
            live = session is { RevokedAt: null } && session.AbsoluteExpiresAt > timeProvider.GetUtcNow();
        }
        catch (Exception ex) when (!requestAborted.IsCancellationRequested)
        {
            // Any failure of the lookup denies the request; nothing here can grant access.
            LogSessionStoreUnavailable(logger, ex, sessionId);
            context.Fail(new LiveSessionFailureReason(this, storeUnavailable: true, "The session store is unavailable."));
            return;
        }

        if (live)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new LiveSessionFailureReason(this, storeUnavailable: false, "The session is not live."));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live-session check for session {SessionId} failed closed: the session store is unavailable")]
    private static partial void LogSessionStoreUnavailable(ILogger logger, Exception exception, Guid sessionId);
}
