using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.ResendConfirmation;

/// <summary>
/// <c>POST /api/v1/auth/resend-confirmation</c> (anonymous, rate limit <c>auth-email</c>): always 202 with a generic
/// body for a valid request (D-23).
/// </summary>
internal sealed class ResendConfirmationEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/resend-confirmation";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (ResendConfirmationCommand command, IUseCaseExecutor<ResendConfirmationCommand, Done> executor, CancellationToken cancellationToken) =>
                (await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false)).ToHttpResult(_ => AcceptedResponse.ToHttpResult()))
            .AllowAnonymous()
            .RequireEmailRateLimiting()
            .WithName("ResendConfirmation")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Sends a new confirmation email to an unconfirmed account.")
            .Produces<AcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();
    }
}
