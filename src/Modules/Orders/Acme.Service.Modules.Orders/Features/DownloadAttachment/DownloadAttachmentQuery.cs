using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.DownloadAttachment;

/// <summary>Reads an attachment of an order: the caller's own, or any order for a manager.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="AttachmentId">The attachment.</param>
/// <param name="Caller">The authenticated caller.</param>
internal sealed record DownloadAttachmentQuery(Guid OrderId, Guid AttachmentId, OrdersCaller Caller) : IQuery<AttachmentDownload>;

/// <summary>
/// Where to get an attachment's content: a time-limited <see cref="DownloadUrl"/> when the blob provider issues one,
/// otherwise the <see cref="Content"/> stream (the receiver disposes it).
/// </summary>
/// <param name="FileName">The stored file name.</param>
/// <param name="ContentType">The detected content type.</param>
/// <param name="DownloadUrl">A provider download URL, or null.</param>
/// <param name="Content">The content, when there is no <paramref name="DownloadUrl"/>.</param>
internal sealed record AttachmentDownload(string FileName, string ContentType, Uri? DownloadUrl, Stream? Content);
