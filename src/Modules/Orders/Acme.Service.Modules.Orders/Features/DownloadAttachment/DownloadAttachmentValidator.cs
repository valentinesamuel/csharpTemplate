using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.DownloadAttachment;

internal sealed class DownloadAttachmentValidator : AbstractValidator<DownloadAttachmentQuery>
{
    public DownloadAttachmentValidator()
    {
        RuleFor(q => q.OrderId).NotEmpty();
        RuleFor(q => q.AttachmentId).NotEmpty();
        RuleFor(q => q.Caller).NotNull();
    }
}
