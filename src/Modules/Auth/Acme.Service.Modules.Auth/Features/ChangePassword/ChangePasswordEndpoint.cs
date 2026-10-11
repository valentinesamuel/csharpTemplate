using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.ChangePassword;

/// <summary>
/// <c>POST /api/v1/auth/change-password</c> (authenticated and <c>live-session</c>, D-29): 204; 400
/// <c>invalid_password</c> for a wrong current password, 400 <c>validation_failed</c> for a rejected new one.
/// </summary>
internal sealed class ChangePasswordEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/change-password";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (ChangePasswordRequest request, HttpContext context, IUseCaseExecutor<ChangePasswordCommand, Done> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var userId) || !AccountHttp.TryGetSessionId(context.User, out var sessionId))
                {
                    return AccountHttp.MissingClaims();
                }

                var command = new ChangePasswordCommand(userId, sessionId, request.CurrentPassword, request.NewPassword);
                var result = await executor.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(_ => TypedResults.NoContent());
            })
            .RequireAuthorization(LiveSessionRequirement.PolicyName)
            .WithName("ChangePassword")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Changes the caller's password and ends every other session.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
