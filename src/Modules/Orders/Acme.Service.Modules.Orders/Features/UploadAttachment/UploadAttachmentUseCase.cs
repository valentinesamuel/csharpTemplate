using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Orders.Features.UploadAttachment;

/// <summary>
/// Attaches a file to an order. 404 <c>order_not_found</c> unless the caller owns the order or manages orders; 400
/// <c>unsupported_attachment_type</c> unless the content starts like a PDF, PNG or JPEG file.
/// </summary>
/// <remarks>
/// Ordering (P17, the documented exception to INV-10): the blob is written FIRST, under
/// <c>orders/{orderId}/{attachmentId}</c>, outside any transaction; then the metadata row is saved in the Orders unit of
/// work. If the unit of work fails, the blob is deleted (compensation, best effort, logged). A blob left behind when the
/// compensation itself fails (or the process dies between the two steps) is an orphan without a metadata row: it is
/// never served, and can be swept by listing keys under <c>orders/</c> that have no <c>order_attachments</c> row. The
/// reverse (a row without its blob) cannot happen, because the row is written only after the blob.
/// </remarks>
internal sealed partial class UploadAttachmentUseCase(
    IOrderRepository orders,
    IRepository<OrderAttachment, Guid> attachments,
    [FromKeyedServices(OrdersModuleKey.Value)] IUnitOfWork unitOfWork,
    IBlobStore blobStore,
    TimeProvider timeProvider,
    ILogger<UploadAttachmentUseCase> logger) : IUseCase<UploadAttachmentCommand, AttachmentResponse>
{
    public async Task<Result<AttachmentResponse>> ExecuteAsync(UploadAttachmentCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var order = await orders.GetForReadAsync(input.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null || !input.Caller.MayAccess(order.CustomerId))
        {
            return OrderErrors.NotFound;
        }

        var contentType = await DetectContentTypeAsync(input.Content, cancellationToken).ConfigureAwait(false);
        if (contentType is null)
        {
            return OrderErrors.UnsupportedAttachmentType;
        }

        var attachment = OrderAttachment.Create(order.Id, input.FileName, contentType, input.Length, input.Caller.UserId, timeProvider.GetUtcNow());
        var key = BlobKey.Create(attachment.BlobKey);

        // 1. The blob, before any transaction opens (INV-10).
        await blobStore.PutAsync(key, input.Content, contentType, cancellationToken).ConfigureAwait(false);

        // 2. The metadata row, in the Orders unit of work; compensate the blob when it does not commit.
        Result saved;
        try
        {
            saved = await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    await attachments.AddAsync(attachment, token).ConfigureAwait(false);
                    return Result.Success();
                },
                retryOnTransientFailure: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CompensateAsync(key, attachment).ConfigureAwait(false);
            throw;
        }

        if (saved.IsFailure)
        {
            await CompensateAsync(key, attachment).ConfigureAwait(false);
            return saved.Error;
        }

        LogAttached(logger, attachment.Id, attachment.OrderId, attachment.ContentType, attachment.SizeBytes);
        return AttachmentResponse.From(attachment);
    }

    private static async Task<string?> DetectContentTypeAsync(Stream content, CancellationToken cancellationToken)
    {
        if (!content.CanSeek)
        {
            throw new ArgumentException("The attachment content must be seekable.", nameof(content));
        }

        var header = new byte[AttachmentContentTypes.HeaderLength];
        content.Position = 0;
        var read = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        return AttachmentContentTypes.Detect(header.AsSpan(0, read));
    }

    private async Task CompensateAsync(BlobKey key, OrderAttachment attachment)
    {
        try
        {
            // Not the request token: the compensation must run even when the request was aborted.
            await blobStore.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            LogCompensated(logger, attachment.Id, attachment.OrderId);
        }
        catch (Exception ex)
        {
            LogCompensationFailed(logger, ex, attachment.Id, attachment.OrderId, key.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Attachment {AttachmentId} ({ContentType}, {SizeBytes} bytes) attached to order {OrderId}")]
    private static partial void LogAttached(ILogger logger, Guid attachmentId, Guid orderId, string contentType, long sizeBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attachment {AttachmentId} of order {OrderId} was not saved; its blob was deleted")]
    private static partial void LogCompensated(ILogger logger, Guid attachmentId, Guid orderId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Attachment {AttachmentId} of order {OrderId} was not saved and its blob {BlobKey} could not be deleted; it is an orphan")]
    private static partial void LogCompensationFailed(ILogger logger, Exception exception, Guid attachmentId, Guid orderId, string blobKey);
}
