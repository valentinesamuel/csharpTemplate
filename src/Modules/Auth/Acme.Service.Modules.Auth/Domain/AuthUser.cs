using Microsoft.AspNetCore.Identity;

namespace Acme.Service.Modules.Auth.Domain;

/// <summary>An Identity user with a Guid v7 key (WH-12), stored in <c>auth.users</c>.</summary>
internal sealed class AuthUser : IdentityUser<Guid>
{
    /// <summary>Maximum length of <see cref="DisplayName"/>.</summary>
    public const int DisplayNameMaxLength = 100;

    public AuthUser()
    {
        Id = Guid.CreateVersion7();
    }

    /// <summary>Optional display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>When the account was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
