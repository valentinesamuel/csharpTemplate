using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.ForgotPassword;

/// <summary>
/// <c>POST /api/v1/auth/forgot-password</c> (anonymous, rate limit <c>auth-email</c>): always 202 with a generic body
/// for a valid request (D-23).
/// </summary>
internal sealed class ForgotPasswordEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/forgot-password";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (ForgotPasswordCommand command, IUseCaseExecutor<ForgotPasswordCommand, Done> executor, CancellationToken cancellationToken) =>
                (await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false)).ToHttpResult(_ => AcceptedResponse.ToHttpResult()))
            .AllowAnonymous()
            .RequireEmailRateLimiting()
            .WithName("ForgotPassword")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Sends a password reset link to a confirmed account.")
            .Produces<AcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();
    }
}
