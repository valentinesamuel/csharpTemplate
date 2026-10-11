using FluentValidation;

namespace Acme.Service.Modules.Orders.Features.UploadAttachment;

internal sealed class UploadAttachmentValidator : AbstractValidator<UploadAttachmentCommand>
{
    public UploadAttachmentValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.Caller).NotNull();
        RuleFor(c => c.Content).NotNull();
        RuleFor(c => c.Length).GreaterThan(0).WithMessage("The file is empty.");
    }
}
