using Acme.Service.BuildingBlocks.Blob;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Blob;

public sealed class BlobKeyTests
{
    [Theory]
    [InlineData("..")]
    [InlineData("../secrets")]
    [InlineData("orders/../auth/keys")]
    [InlineData("orders/..")]
    [InlineData("/etc/passwd")]
    [InlineData("orders\\..\\x")]
    [InlineData("orders/a b.pdf")]
    [InlineData("orders/%2e%2e/x")]
    [InlineData("orders//x")]
    [InlineData("orders/./x")]
    [InlineData("orders/")]
    [InlineData("")]
    public void TryCreate_TraversalOrDisallowedKey_ReturnsFalse(string value)
    {
        BlobKey.TryCreate(value, out var key).ShouldBeFalse();
        key.ShouldBeNull();
        Should.Throw<ArgumentException>(() => BlobKey.Create(value));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("orders/0190a5c4-0000-7000-8000-000000000001/invoice.pdf")]
    [InlineData("Avatars/user_1/photo.v2.PNG")]
    public void Create_ValidKey_ReturnsKeyWithValue(string value)
    {
        var key = BlobKey.Create(value);

        key.Value.ShouldBe(value);
        key.ToString().ShouldBe(value);
    }

    [Fact]
    public void TryCreate_KeyLongerThanMax_ReturnsFalse()
    {
        BlobKey.TryCreate(new string('a', BlobKey.MaxLength + 1), out _).ShouldBeFalse();
    }
}
