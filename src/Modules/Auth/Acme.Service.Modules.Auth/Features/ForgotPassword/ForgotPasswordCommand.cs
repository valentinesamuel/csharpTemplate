using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;

namespace Acme.Service.Modules.Auth.Features.ForgotPassword;

/// <summary><c>POST /api/v1/auth/forgot-password</c> (body bound as is).</summary>
/// <param name="Email">The address of the account whose password is forgotten.</param>
internal sealed record ForgotPasswordCommand(string Email) : ICommand<Done>, IEmailRateLimited
{
    string? IEmailRateLimited.Email => Email;
}
