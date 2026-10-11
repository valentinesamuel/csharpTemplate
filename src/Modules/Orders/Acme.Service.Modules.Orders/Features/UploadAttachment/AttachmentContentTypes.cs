namespace Acme.Service.Modules.Orders.Features.UploadAttachment;

/// <summary>
/// The accepted attachment types (PDF, PNG, JPEG), recognised by their leading "magic" bytes. The client's
/// <c>Content-Type</c> header and file extension are never trusted.
/// </summary>
internal static class AttachmentContentTypes
{
    public const string Pdf = "application/pdf";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    /// <summary>Bytes needed to recognise every accepted type.</summary>
    public const int HeaderLength = 8;

    private static ReadOnlySpan<byte> PdfSignature => "%PDF-"u8;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    /// <summary>The content type of a file starting with <paramref name="header"/>, or null when it is not accepted.</summary>
    public static string? Detect(ReadOnlySpan<byte> header) =>
        header.StartsWith(PdfSignature) ? Pdf
        : header.StartsWith(PngSignature) ? Png
        : header.StartsWith(JpegSignature) ? Jpeg
        : null;
}
