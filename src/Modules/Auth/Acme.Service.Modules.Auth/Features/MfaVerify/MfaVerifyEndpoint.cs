using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.MfaVerify;

/// <summary>
/// <c>POST /api/v1/auth/mfa/authenticator/verify</c> (authenticated and <c>live-session</c>, D-29): 200 with ten recovery
/// codes (shown once), 400 <c>invalid_code</c>, or 409 <c>mfa_already_enabled</c>.
/// </summary>
internal sealed class MfaVerifyEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/mfa/authenticator/verify";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (MfaVerifyRequest request, HttpContext context, IUseCaseExecutor<MfaVerifyCommand, RecoveryCodesResponse> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var userId))
                {
                    return AccountHttp.MissingClaims();
                }

                return (await executor.ExecuteAsync(new MfaVerifyCommand(userId, request.Code), context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization(LiveSessionRequirement.PolicyName)
            .WithName("VerifyAuthenticator")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Enables two-factor authentication with a code of the new authenticator key and returns recovery codes once.")
            .Produces<RecoveryCodesResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
