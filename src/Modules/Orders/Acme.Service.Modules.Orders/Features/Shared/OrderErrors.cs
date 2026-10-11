using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.Modules.Orders.Features.Shared;

/// <summary>Client-visible failures of the order endpoints (besides <c>Order.InvalidTransition</c>).</summary>
internal static class OrderErrors
{
    /// <summary>
    /// No such order, it was deleted, or the caller may not see it (404). Another customer's order is reported as missing,
    /// never as forbidden, so ids cannot be probed.
    /// </summary>
    public static readonly Error NotFound = Error.NotFound("order_not_found", "The order does not exist.");

    /// <summary>The order changed since the caller read it (409); same code as a lost optimistic concurrency race.</summary>
    public static readonly Error ConcurrencyConflict = Error.Conflict(
        "concurrency_conflict", "The order was modified by another request. Reload it and try again.");

    /// <summary>A list filter could not be parsed (400).</summary>
    public const string InvalidFilterCode = "invalid_filter";

    /// <summary>No such attachment on the order, or the caller may not see the order (404).</summary>
    public static readonly Error AttachmentNotFound = Error.NotFound("attachment_not_found", "The attachment does not exist.");

    /// <summary>The file content is not one of the accepted types, judged by its leading bytes (400).</summary>
    public static readonly Error UnsupportedAttachmentType = Error.Validation(
        "unsupported_attachment_type", "The file must be a PDF, PNG or JPEG document.");

    /// <summary>The upload is not a multipart form with one non-empty <c>file</c> part (400).</summary>
    public const string InvalidAttachmentCode = "invalid_attachment";

    /// <summary>The upload exceeds <c>Orders:Attachments:MaxBytes</c> (413).</summary>
    public const string AttachmentTooLargeCode = "attachment_too_large";
}
