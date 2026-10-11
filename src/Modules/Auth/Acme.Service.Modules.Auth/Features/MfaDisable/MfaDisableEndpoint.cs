using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.MfaDisable;

/// <summary>
/// <c>POST /api/v1/auth/mfa/disable</c> (authenticated and <c>live-session</c>, D-29): 204, or 400
/// <c>invalid_password</c>.
/// </summary>
internal sealed class MfaDisableEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/mfa/disable";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (PasswordConfirmationRequest request, HttpContext context, IUseCaseExecutor<MfaDisableCommand, Done> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var userId))
                {
                    return AccountHttp.MissingClaims();
                }

                var result = await executor.ExecuteAsync(new MfaDisableCommand(userId, request.Password), context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(_ => TypedResults.NoContent());
            })
            .RequireAuthorization(LiveSessionRequirement.PolicyName)
            .WithName("DisableMfa")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Disables two-factor authentication after confirming the password, and discards the authenticator key and recovery codes.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
