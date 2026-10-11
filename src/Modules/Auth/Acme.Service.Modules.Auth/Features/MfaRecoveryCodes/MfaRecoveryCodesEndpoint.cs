using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.MfaRecoveryCodes;

/// <summary>
/// <c>POST /api/v1/auth/mfa/recovery-codes</c> (authenticated and <c>live-session</c>, D-29): 200 with ten new codes
/// (shown once; the old ones stop working), 400 <c>invalid_password</c>, or 409 <c>mfa_not_enabled</c>.
/// </summary>
internal sealed class MfaRecoveryCodesEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/mfa/recovery-codes";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (PasswordConfirmationRequest request, HttpContext context, IUseCaseExecutor<MfaRecoveryCodesCommand, RecoveryCodesResponse> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var userId))
                {
                    return AccountHttp.MissingClaims();
                }

                var command = new MfaRecoveryCodesCommand(userId, request.Password);
                return (await executor.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization(LiveSessionRequirement.PolicyName)
            .WithName("RegenerateRecoveryCodes")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Replaces the recovery codes after confirming the password and returns the new ones once.")
            .Produces<RecoveryCodesResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
