using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;

namespace Acme.Service.Modules.Auth.Features.ResendConfirmation;

/// <summary><c>POST /api/v1/auth/resend-confirmation</c> (body bound as is).</summary>
/// <param name="Email">The address to send a new confirmation link to.</param>
internal sealed record ResendConfirmationCommand(string Email) : ICommand<Done>, IEmailRateLimited
{
    string? IEmailRateLimited.Email => Email;
}
