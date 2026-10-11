using Acme.Service.BuildingBlocks.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Acme.Service.Modules.Auth.Tokens;

/// <summary>
/// <c>GET /.well-known/jwks.json</c>: the public part of every key of the ring (Active, Next, Retiring) with
/// <c>kid</c>, <c>alg=RS256</c> and <c>use=sig</c>. Anonymous (listed in the anonymous endpoint allowlist, INV-12)
/// and cacheable for 5 minutes, which is the minimum wait between rotation steps (D-08).
/// </summary>
internal sealed class JwksEndpoint : IEndpoint
{
    public const string Path = "/.well-known/jwks.json";

    public const string CacheControl = "public,max-age=300";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, (SigningKeyRing keyRing, HttpContext context) =>
            {
                context.Response.Headers[HeaderNames.CacheControl] = CacheControl;
                return TypedResults.Text(keyRing.JwksJson, "application/json");
            })
            .AllowAnonymous()
            .WithName("GetJwks")
            .WithTags("Auth")
            .WithSummary("JSON Web Key Set of the token signing keys.");
    }
}
