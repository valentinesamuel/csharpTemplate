using System.Buffers.Text;
using System.Security.Cryptography;
using Acme.Service.Modules.Auth.Sessions;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Tokens;

public sealed class RefreshTokenFormatTests
{
    [Fact]
    public void TryHash_WellFormedToken_ReturnsSha256OfBytes()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);

        RefreshTokenService.TryHash(Base64Url.EncodeToString(bytes), out var hash).ShouldBeTrue();

        hash.ShouldBe(SHA256.HashData(bytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("eyJhbGciOiJSUzI1NiIsInR5cCI6ImF0K2p3dCJ9.e30.sig")]
    public void TryHash_MalformedToken_ReturnsFalse(string? token)
    {
        RefreshTokenService.TryHash(token, out _).ShouldBeFalse();
    }
}
