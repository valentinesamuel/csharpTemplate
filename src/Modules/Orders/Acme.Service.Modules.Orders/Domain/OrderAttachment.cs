using System.Globalization;

namespace Acme.Service.Modules.Orders.Domain;

/// <summary>
/// Metadata of a file attached to an <see cref="Order"/> (<c>orders.order_attachments</c>). The content lives in the blob
/// store under <see cref="BlobKey"/> (<c>orders/{orderId}/{attachmentId}</c>); it is written before this row (P17).
/// </summary>
internal sealed class OrderAttachment
{
    /// <summary>Longest stored file name.</summary>
    public const int FileNameMaxLength = 255;

    /// <summary>Longest stored content type.</summary>
    public const int ContentTypeMaxLength = 100;

    /// <summary>File name stored when the client sent none that is usable.</summary>
    public const string DefaultFileName = "attachment";

    private OrderAttachment(Guid id, Guid orderId, string fileName, string contentType, long sizeBytes, Guid uploadedBy, DateTimeOffset createdAt)
    {
        Id = id;
        OrderId = orderId;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        UploadedBy = uploadedBy;
        CreatedAt = createdAt;
    }

    // For the persistence mapping.
    private OrderAttachment()
    {
        FileName = string.Empty;
        ContentType = string.Empty;
    }

    /// <summary>Attachment id (Guid v7).</summary>
    public Guid Id { get; private set; }

    /// <summary>The order the file is attached to.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>The client's file name, reduced to a safe display name.</summary>
    public string FileName { get; private set; }

    /// <summary>The content type detected from the file's content (never the client's header).</summary>
    public string ContentType { get; private set; }

    /// <summary>Size of the content in bytes.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>The user who uploaded the file.</summary>
    public Guid UploadedBy { get; private set; }

    /// <summary>When the file was attached (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The blob key of the content: <c>orders/{orderId}/{attachmentId}</c>.</summary>
    public string BlobKey => KeyOf(OrderId, Id);

    /// <summary>Creates the metadata of a new attachment of <paramref name="orderId"/>.</summary>
    /// <exception cref="ArgumentException">An id or the content type is missing, or the size is not positive.</exception>
    public static OrderAttachment Create(Guid orderId, string? fileName, string contentType, long sizeBytes, Guid uploadedBy, DateTimeOffset now)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("The order is required.", nameof(orderId));
        }

        if (uploadedBy == Guid.Empty)
        {
            throw new ArgumentException("The uploader is required.", nameof(uploadedBy));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);

        return new OrderAttachment(Guid.CreateVersion7(now), orderId, SafeFileName(fileName), contentType, sizeBytes, uploadedBy, now);
    }

    /// <summary>The blob key of attachment <paramref name="attachmentId"/> of order <paramref name="orderId"/>.</summary>
    public static string KeyOf(Guid orderId, Guid attachmentId) =>
        string.Create(CultureInfo.InvariantCulture, $"orders/{orderId:D}/{attachmentId:D}");

    /// <summary>
    /// The last path segment of <paramref name="fileName"/> without control characters, at most
    /// <see cref="FileNameMaxLength"/> characters; <see cref="DefaultFileName"/> when nothing usable remains.
    /// </summary>
    public static string SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return DefaultFileName;
        }

        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (name.Length > FileNameMaxLength)
        {
            name = name[..FileNameMaxLength];
        }

        return name.Length == 0 || name is "." or ".." ? DefaultFileName : name;
    }
}
