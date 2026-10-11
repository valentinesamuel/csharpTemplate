using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;

namespace Acme.Service.Modules.Auth.Features.Register;

/// <summary><c>POST /api/v1/auth/register</c>: creates an account (body bound as is).</summary>
/// <param name="Email">The email address, also the user name.</param>
/// <param name="Password">The password (NIST 800-63B length policy, no composition rules).</param>
/// <param name="DisplayName">Optional display name.</param>
internal sealed record RegisterCommand(string Email, string Password, string? DisplayName) : ICommand<Done>, IEmailRateLimited
{
    string? IEmailRateLimited.Email => Email;

    // Never print the password (INV-28).
    public override string ToString() => nameof(RegisterCommand);
}
