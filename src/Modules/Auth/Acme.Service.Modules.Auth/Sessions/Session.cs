namespace Acme.Service.Modules.Auth.Sessions;

/// <summary>Reasons stored in <c>auth.sessions.revoked_reason</c>.</summary>
internal static class SessionRevocationReasons
{
    /// <summary>A used refresh token of the session was presented again (D-11, INV-15).</summary>
    public const string ReuseDetected = "reuse_detected";

    /// <summary>The user is locked, deleted, or no longer allowed to sign in.</summary>
    public const string UserInactive = "user_inactive";

    /// <summary>The user signed out of this session.</summary>
    public const string Logout = "logout";

    /// <summary>The user signed out of every session.</summary>
    public const string LogoutAll = "logout_all";

    /// <summary>The user changed the password; every other session ends.</summary>
    public const string PasswordChanged = "password_changed";

    /// <summary>The password was reset through an emailed link; every session ends.</summary>
    public const string PasswordReset = "password_reset";

    /// <summary>An administrator locked the account; every session ends.</summary>
    public const string UserLocked = "user_locked";
}

/// <summary>
/// A sign-in session, stored in <c>auth.sessions</c>; its id is the refresh token family id and the <c>sid</c>
/// claim of every access token issued in it. Revoking it invalidates every refresh token of the family and, for
/// endpoints with the <c>live-session</c> policy, every outstanding access token (D-29).
/// </summary>
internal sealed class Session
{
    /// <summary>Maximum length of <see cref="RevokedReason"/>.</summary>
    public const int RevokedReasonMaxLength = 64;

    public Session(Guid userId, DateTimeOffset createdAt, DateTimeOffset absoluteExpiresAt, IEnumerable<string> amr)
    {
        Id = Guid.CreateVersion7();
        UserId = userId;
        CreatedAt = createdAt;
        AbsoluteExpiresAt = absoluteExpiresAt;
        Amr = [.. amr];
    }

    // EF Core materialization.
    private Session()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The session ends at this time, whatever the refresh activity.</summary>
    public DateTimeOffset AbsoluteExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    /// <summary>Authentication methods used at sign-in (RFC 8176, for example <c>pwd</c>, <c>mfa</c>).</summary>
    public List<string> Amr { get; private set; } = [];

    /// <summary>True when the session is neither revoked nor past its absolute expiry.</summary>
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < AbsoluteExpiresAt;

    /// <summary>Revokes the session. Returns false when it was already revoked (the first reason is kept).</summary>
    public bool Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return false;
        }

        RevokedAt = now;
        RevokedReason = reason;
        return true;
    }
}
