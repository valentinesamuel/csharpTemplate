using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.DownloadAttachment;

/// <summary>
/// Locates an attachment's content: a download URL valid for <see cref="DownloadUrlLifetime"/> when the blob provider
/// supports one, otherwise an open stream. 404 <c>order_not_found</c> unless the caller owns the order or manages orders;
/// 404 <c>attachment_not_found</c> when the order has no such attachment (or its blob is missing).
/// </summary>
internal sealed class DownloadAttachmentUseCase(
    IOrderRepository orders,
    IRepository<OrderAttachment, Guid> attachments,
    IBlobStore blobStore) : IUseCase<DownloadAttachmentQuery, AttachmentDownload>
{
    /// <summary>How long an issued download URL stays valid.</summary>
    public static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(5);

    public async Task<Result<AttachmentDownload>> ExecuteAsync(DownloadAttachmentQuery input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var order = await orders.GetForReadAsync(input.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null || !input.Caller.MayAccess(order.CustomerId))
        {
            return OrderErrors.NotFound;
        }

        var attachment = await attachments.GetByIdAsync(input.AttachmentId, cancellationToken).ConfigureAwait(false);
        if (attachment is null || attachment.OrderId != order.Id)
        {
            return OrderErrors.AttachmentNotFound;
        }

        var key = BlobKey.Create(attachment.BlobKey);
        var url = await blobStore.GetDownloadUrlAsync(key, DownloadUrlLifetime, cancellationToken).ConfigureAwait(false);
        if (url is not null)
        {
            return new AttachmentDownload(attachment.FileName, attachment.ContentType, url, null);
        }

        var content = await blobStore.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
        return content is null
            ? OrderErrors.AttachmentNotFound
            : new AttachmentDownload(attachment.FileName, attachment.ContentType, null, content);
    }
}
