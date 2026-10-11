using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.Register;

/// <summary>
/// <c>POST /api/v1/auth/register</c> (anonymous, rate limit <c>auth-email</c>): always 202 with a generic body for a
/// valid request, whether or not the email is taken (D-23); 400 for an invalid request.
/// </summary>
internal sealed class RegisterEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/register";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (RegisterCommand command, IUseCaseExecutor<RegisterCommand, Done> executor, CancellationToken cancellationToken) =>
            {
                var result = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

                // A concurrent registration of the same email loses on the unique index: still the generic answer (D-23).
                if (result.IsFailure && result.Error.Type == ErrorType.Conflict)
                {
                    return AcceptedResponse.ToHttpResult();
                }

                return result.ToHttpResult(_ => AcceptedResponse.ToHttpResult());
            })
            .AllowAnonymous()
            .RequireEmailRateLimiting()
            .WithName("Register")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Registers an account; a confirmation email is sent.")
            .Produces<AcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();
    }
}
