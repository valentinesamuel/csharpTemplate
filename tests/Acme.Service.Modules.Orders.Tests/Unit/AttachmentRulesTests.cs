using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.UploadAttachment;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Unit;

public sealed class AttachmentRulesTests
{
    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, "application/pdf")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, "image/jpeg")]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, null)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 }, null)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, null)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, null)]
    [InlineData(new byte[0], null)]
    public void Detect_LeadingBytes_ReturnsTheAcceptedTypeOrNull(byte[] header, string? expected) =>
        AttachmentContentTypes.Detect(header).ShouldBe(expected);

    [Theory]
    [InlineData("invoice.pdf", "invoice.pdf")]
    [InlineData("C:\\Users\\me\\invoice.pdf", "invoice.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a\u0000b\nc.png", "abc.png")]
    [InlineData("  ", "attachment")]
    [InlineData(null, "attachment")]
    [InlineData("dir/..", "attachment")]
    public void SafeFileName_ClientName_IsReducedToASafeDisplayName(string? fileName, string expected) =>
        OrderAttachment.SafeFileName(fileName).ShouldBe(expected);

    [Fact]
    public void SafeFileName_TooLong_IsTruncated() =>
        OrderAttachment.SafeFileName(new string('x', 400) + ".pdf").Length.ShouldBe(OrderAttachment.FileNameMaxLength);

    [Fact]
    public void Create_BlobKey_IsOrdersOrderIdAttachmentId()
    {
        var orderId = Guid.CreateVersion7();

        var attachment = OrderAttachment.Create(orderId, "a.pdf", "application/pdf", 10, Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);

        attachment.BlobKey.ShouldBe($"orders/{orderId}/{attachment.Id}");
        Acme.Service.BuildingBlocks.Blob.BlobKey.TryCreate(attachment.BlobKey, out _).ShouldBeTrue();
    }

    [Fact]
    public void CustomerSnapshot_Apply_IgnoresOlderDetails()
    {
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var snapshot = CustomerSnapshot.Create(Guid.CreateVersion7(), "new@example.com", " New ", at);

        snapshot.DisplayName.ShouldBe("New");
        snapshot.Apply("old@example.com", null, at.AddMinutes(-1)).ShouldBeFalse();
        snapshot.Email.ShouldBe("new@example.com");
        snapshot.Apply("newer@example.com", null, at.AddMinutes(1)).ShouldBeTrue();
        snapshot.Email.ShouldBe("newer@example.com");
        snapshot.DisplayName.ShouldBeNull();
        snapshot.UpdatedAt.ShouldBe(at.AddMinutes(1));
    }
}
