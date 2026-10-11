using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.ResetPassword;

/// <summary>
/// <c>POST /api/v1/auth/reset-password</c> (anonymous, rate limit <c>auth-credentials</c>): 204, 400
/// <c>invalid_token</c>, or 400 <c>validation_failed</c> for a rejected new password.
/// </summary>
internal sealed class ResetPasswordEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/reset-password";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (ResetPasswordCommand command, IUseCaseExecutor<ResetPasswordCommand, Done> executor, CancellationToken cancellationToken) =>
                (await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false)).ToHttpResult(_ => TypedResults.NoContent()))
            .AllowAnonymous()
            .RequireCredentialsRateLimiting()
            .WithName("ResetPassword")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Sets a new password with the token of the emailed reset link and ends every session.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem();
    }
}
