namespace Acme.Service.Modules.Auth.Sessions;

/// <summary>
/// A refresh token of a session, stored in <c>auth.refresh_tokens</c>. Only the SHA-256 hash of the token is stored
/// (INV-15); the token itself (32 random bytes, base64url) is returned to the client once. Single-use: using it sets
/// <see cref="UsedAt"/> and links the replacement through <see cref="ReplacedById"/> (D-11).
/// </summary>
internal sealed class RefreshToken
{
    /// <summary>Length of <see cref="TokenHash"/> (SHA-256).</summary>
    public const int HashLength = 32;

    /// <summary>Maximum length of <see cref="CreatedIp"/> (an IPv6 address in text form).</summary>
    public const int CreatedIpMaxLength = 45;

    /// <summary>Maximum stored length of <see cref="UserAgent"/>; longer values are truncated.</summary>
    public const int UserAgentMaxLength = 512;

    public RefreshToken(Guid sessionId, byte[] tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, string? createdIp, string? userAgent)
    {
        Id = Guid.CreateVersion7();
        SessionId = sessionId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        CreatedIp = createdIp is { Length: <= CreatedIpMaxLength } ? createdIp : null;
        UserAgent = userAgent is { Length: > UserAgentMaxLength } ? userAgent[..UserAgentMaxLength] : userAgent;
    }

    // EF Core materialization.
    private RefreshToken()
    {
        TokenHash = [];
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    /// <summary>SHA-256 of the token bytes; unique.</summary>
    public byte[] TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the token was exchanged; null while unused.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>The token issued in exchange for this one.</summary>
    public Guid? ReplacedById { get; set; }

    public string? CreatedIp { get; private set; }

    public string? UserAgent { get; private set; }
}
