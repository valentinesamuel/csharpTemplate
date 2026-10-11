using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Errors;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Features.Shared;
using Acme.Service.Modules.Orders.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Orders.Features.UploadAttachment;

/// <summary>
/// <c>POST /api/v1/orders/{id}/attachments</c> (the owner with <c>orders.create</c>, or <c>orders.manage</c>): a
/// <c>multipart/form-data</c> upload of one PDF, PNG or JPEG file in the part <c>file</c>, at most
/// <c>Orders:Attachments:MaxBytes</c> (10 MiB by default). 201 with <c>Location</c>; 413 <c>attachment_too_large</c>
/// when larger; 400 for anything else that is not an accepted file.
/// </summary>
/// <remarks>
/// The size is enforced three times: the request body limit (servers that support it, such as Kestrel, stop reading at
/// <c>MaxBytes</c> plus <see cref="MultipartOverheadBytes"/>), a declared <c>Content-Length</c> above that limit (rejected
/// before reading), and the length of the file part itself.
/// </remarks>
internal sealed class UploadAttachmentEndpoint : IEndpoint
{
    public const string Path = OrdersHttp.ItemPath + "/attachments";

    /// <summary>The form part that carries the file.</summary>
    public const string FileField = "file";

    /// <summary>Room for the multipart boundaries and part headers on top of the file itself.</summary>
    public const long MultipartOverheadBytes = 64 * 1024;

    public void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var maxBytes = endpoints.ServiceProvider.GetRequiredService<IOptions<OrdersOptions>>().Value.Attachments.MaxBytes;

        endpoints.MapPost(Path, async (Guid id, HttpContext context, IUseCaseExecutor<UploadAttachmentCommand, AttachmentResponse> executor) =>
            {
                if (!OrdersCaller.TryFrom(context.User, out var caller))
                {
                    return OrdersHttp.MissingClaims();
                }

                var request = context.Request;
                if (request.ContentLength > maxBytes + MultipartOverheadBytes)
                {
                    return TooLarge(maxBytes);
                }

                if (!request.HasFormContentType)
                {
                    return Invalid("The request must be multipart/form-data with the file in the part 'file'.");
                }

                IFormCollection form;
                try
                {
                    context.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions
                    {
                        MultipartBodyLengthLimit = maxBytes + MultipartOverheadBytes,
                        ValueCountLimit = 16,
                    }));
                    form = await request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return TooLarge(maxBytes);
                }
                catch (InvalidDataException)
                {
                    return Invalid("The multipart body could not be read.");
                }

                var file = form.Files.GetFile(FileField);
                if (file is null)
                {
                    return Invalid("The file must be sent in the multipart part 'file'.");
                }

                if (file.Length > maxBytes)
                {
                    return TooLarge(maxBytes);
                }

                var content = file.OpenReadStream();
                await using (content.ConfigureAwait(false))
                {
                    var command = new UploadAttachmentCommand(id, caller, file.FileName, file.Length, content);
                    var result = await executor.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false);
                    return result.ToHttpResult(attachment => TypedResults.Created(DownloadPathOf(id, attachment.Id), attachment));
                }
            })
            .RequireAnyPermission(OrdersPermissions.Create, OrdersPermissions.Manage)
            .WithMetadata(new RequestBodySizeLimit(maxBytes + MultipartOverheadBytes))
            .WithOrdersMetadata("UploadOrderAttachment", "Attaches a PDF, PNG or JPEG file to an order.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<AttachmentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }

    /// <summary>The download path of attachment <paramref name="attachmentId"/> of order <paramref name="orderId"/>.</summary>
    public static string DownloadPathOf(Guid orderId, Guid attachmentId) => $"{OrdersHttp.PathOf(orderId)}/attachments/{attachmentId}";

    private static IResult Invalid(string message) =>
        Result.Failure(Error.Validation(OrderErrors.InvalidAttachmentCode, message)).ToHttpResult();

    private static ProblemHttpResult TooLarge(long maxBytes) => TypedResults.Problem(
        detail: $"The file must be at most {maxBytes} bytes.",
        statusCode: StatusCodes.Status413PayloadTooLarge,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { [ProblemDetailsSetup.CodeKey] = OrderErrors.AttachmentTooLargeCode });

    /// <summary>Caps the request body (honoured by servers that support it, such as Kestrel).</summary>
    private sealed class RequestBodySizeLimit(long maxBytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => maxBytes;
    }
}
