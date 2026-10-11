using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.UploadAttachment;

/// <summary>
/// Attaches a file to an order: the caller's own (with <c>orders.create</c>), or any order for a manager.
/// </summary>
/// <remarks>
/// Deliberately not an <c>ICommand</c>: the transaction behaviour would open the unit of work before the use case runs,
/// and the blob must be written BEFORE the transaction opens (INV-10's documented exception). The use case runs its
/// own unit of work after the blob write.
/// </remarks>
/// <param name="OrderId">The order.</param>
/// <param name="Caller">The authenticated caller.</param>
/// <param name="FileName">The client's file name (metadata only).</param>
/// <param name="Length">Size of <paramref name="Content"/> in bytes.</param>
/// <param name="Content">The file content; seekable, positioned at the start, owned by the caller.</param>
internal sealed record UploadAttachmentCommand(Guid OrderId, OrdersCaller Caller, string? FileName, long Length, Stream Content);

/// <summary>An attachment's metadata, as returned by the upload endpoint.</summary>
/// <param name="Id">Attachment id.</param>
/// <param name="OrderId">The order.</param>
/// <param name="FileName">The stored file name.</param>
/// <param name="ContentType">The detected content type.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="CreatedAt">When it was attached (UTC).</param>
internal sealed record AttachmentResponse(Guid Id, Guid OrderId, string FileName, string ContentType, long SizeBytes, DateTimeOffset CreatedAt)
{
    public static AttachmentResponse From(OrderAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return new AttachmentResponse(
            attachment.Id, attachment.OrderId, attachment.FileName, attachment.ContentType, attachment.SizeBytes, attachment.CreatedAt);
    }
}
