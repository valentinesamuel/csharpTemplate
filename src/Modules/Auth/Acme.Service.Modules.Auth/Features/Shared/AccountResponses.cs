using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Http;

namespace Acme.Service.Modules.Auth.Features.Shared;

/// <summary>The success value of account commands that return no data.</summary>
internal readonly record struct Done
{
    public static readonly Done Value;
}

/// <summary>
/// The generic 202 body of register and resend-confirmation: identical whether or not an account exists (D-23).
/// </summary>
/// <param name="Message">A message that reveals nothing about the account.</param>
internal sealed record AcceptedResponse(string Message)
{
    public static readonly AcceptedResponse CheckInbox = new(
        "If the address can receive email, a message with further instructions has been sent.");

    /// <summary>202 Accepted with <see cref="CheckInbox"/>.</summary>
    public static IResult ToHttpResult() => EnvelopeResult.Wrap(TypedResults.Accepted((string?)null, CheckInbox));
}

/// <summary>Tokens returned by sign-in and refresh (D-10: in the JSON body).</summary>
/// <param name="AccessToken">RS256 access token (<c>typ=at+jwt</c>).</param>
/// <param name="AccessTokenExpiresAt">Expiry of the access token (UTC).</param>
/// <param name="RefreshToken">Single-use refresh token (D-11).</param>
/// <param name="RefreshTokenExpiresAt">Expiry of the refresh token (UTC).</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
internal sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string TokenType)
{
    public const string BearerType = "Bearer";

    public static TokenResponse From(TokenPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return new TokenResponse(pair.AccessToken, pair.AccessTokenExpiresAt, pair.RefreshToken, pair.RefreshTokenExpiresAt, BearerType);
    }

    // Never print token values (INV-28).
    public override string ToString() => nameof(TokenResponse);
}
