namespace Acme.Service.Modules.Auth.Contracts;

/// <summary>A minimal, read-only view of a user for other modules.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Email">The user's email address.</param>
/// <param name="DisplayName">The display name, when the user set one.</param>
/// <param name="IsActive">False while the account is locked out.</param>
public sealed record UserSummary(Guid Id, string Email, string? DisplayName, bool IsActive);
