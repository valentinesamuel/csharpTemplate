using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Authorization;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Orders.Features.DownloadAttachment;

/// <summary>
/// <c>GET /api/v1/orders/{id}/attachments/{attachmentId}</c> (<c>orders.read</c>; the owner, or anyone with
/// <c>orders.manage</c>): 302 to a time-limited provider download URL when the blob provider supports it, otherwise the
/// content streamed with its detected content type as an attachment download.
/// </summary>
internal sealed class DownloadAttachmentEndpoint : IEndpoint
{
    public const string Path = OrdersHttp.ItemPath + "/attachments/{attachmentId:guid}";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, async (Guid id, Guid attachmentId, HttpContext context, IUseCaseExecutor<DownloadAttachmentQuery, AttachmentDownload> executor) =>
            {
                if (!OrdersCaller.TryFrom(context.User, out var caller))
                {
                    return OrdersHttp.MissingClaims();
                }

                var result = await executor.ExecuteAsync(new DownloadAttachmentQuery(id, attachmentId, caller), context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(download => download.DownloadUrl is { } url
                    ? TypedResults.Redirect(url.AbsoluteUri)
                    : TypedResults.Stream(download.Content!, download.ContentType, download.FileName));
            })
            .RequirePermission(OrdersPermissions.Read)
            .WithOrdersMetadata("DownloadOrderAttachment", "Downloads an attachment of an order (302 to a download URL, or the content).")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
