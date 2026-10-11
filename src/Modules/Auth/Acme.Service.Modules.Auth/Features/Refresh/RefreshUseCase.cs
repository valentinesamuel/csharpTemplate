using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;

namespace Acme.Service.Modules.Auth.Features.Refresh;

/// <summary>
/// Exchanges a refresh token for a new token pair in the same session (P12 algorithm, single use, reuse revokes the
/// session). Every client failure is 401 <c>invalid_refresh_token</c>; a database outage is 503.
/// </summary>
internal sealed class RefreshUseCase(RefreshTokenService refreshTokens) : IUseCase<RefreshCommand, TokenResponse>
{
    public async Task<Result<TokenResponse>> ExecuteAsync(RefreshCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var pair = await refreshTokens.RefreshAsync(input.RefreshToken, input.Client, cancellationToken).ConfigureAwait(false);
        return pair.IsSuccess ? TokenResponse.From(pair.Value) : Result.Failure<TokenResponse>(pair.Error);
    }
}
