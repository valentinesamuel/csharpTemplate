using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.ConfirmEmail;

/// <summary><c>POST /api/v1/auth/confirm-email</c> (anonymous): 204, or 400 <c>invalid_token</c>.</summary>
internal sealed class ConfirmEmailEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/confirm-email";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (ConfirmEmailCommand command, IUseCaseExecutor<ConfirmEmailCommand, Done> executor, CancellationToken cancellationToken) =>
                (await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false)).ToHttpResult(_ => TypedResults.NoContent()))
            .AllowAnonymous()
            .WithName("ConfirmEmail")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Confirms an email address with the token of the emailed link.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem();
    }
}
