using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.MfaSetup;

/// <summary>
/// <c>POST /api/v1/auth/mfa/authenticator</c> (authenticated and <c>live-session</c>, D-29): 200 with a new key, or 409
/// <c>mfa_already_enabled</c>.
/// </summary>
internal sealed class MfaSetupEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/mfa/authenticator";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (HttpContext context, IUseCaseExecutor<MfaSetupCommand, MfaSetupResponse> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var userId))
                {
                    return AccountHttp.MissingClaims();
                }

                return (await executor.ExecuteAsync(new MfaSetupCommand(userId), context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization(LiveSessionRequirement.PolicyName)
            .WithName("SetUpAuthenticator")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Creates a new authenticator key for the caller; verify a code to enable two-factor authentication.")
            .Produces<MfaSetupResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
