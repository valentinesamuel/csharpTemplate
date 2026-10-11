using System.Security.Cryptography;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Security;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Tokens;

/// <summary>
/// An invalid key ring aborts startup with exit code 1 and messages naming the configuration keys (INV-13, INV-17); it
/// never degrades to accepting tokens, and never echoes key material (INV-11, INV-28).
/// </summary>
public sealed class KeyRingStartupTests
{
    [Fact]
    public async Task Start_TwoActiveKeys_ExitsWith1NamingKey()
    {
        var second = TestSigningKeys.CreatePrivateKeyBase64();

        var (exitCode, output) = await HostProcess.RunAsync(new Dictionary<string, string>
        {
            ["Auth__Jwt__SigningKeys__1__Kid"] = "second",
            ["Auth__Jwt__SigningKeys__1__Status"] = "Active",
            ["Auth__Jwt__SigningKeys__1__PrivateKeyBase64"] = second,
        });

        exitCode.ShouldBe(1);
        output.ShouldContain("Auth:Jwt:SigningKeys must contain exactly one key with Status=Active (found 2)");
        output.ShouldNotContain(second[..40]);
        output.ShouldNotContain(TestSigningKeys.DefaultPrivateKeyBase64[..40]);
    }

    [Fact]
    public async Task Start_NoActiveKey_ExitsWith1NamingKey()
    {
        var (exitCode, output) = await HostProcess.RunAsync(new Dictionary<string, string>
        {
            ["Auth__Jwt__SigningKeys__0__Status"] = "Retiring",
        });

        exitCode.ShouldBe(1);
        output.ShouldContain("Auth:Jwt:SigningKeys must contain exactly one key with Status=Active (found 0)");
    }

    [Fact]
    public async Task Start_NoKeysConfigured_ExitsWith1NamingKey()
    {
        var (exitCode, output) = await HostProcess.RunAsync(new Dictionary<string, string>
        {
            ["Auth__Jwt__SigningKeys__0__Kid"] = string.Empty,
            ["Auth__Jwt__SigningKeys__0__Status"] = string.Empty,
            ["Auth__Jwt__SigningKeys__0__PrivateKeyBase64"] = string.Empty,
        });

        exitCode.ShouldBe(1);
        output.ShouldContain("Auth:Jwt:SigningKeys must contain exactly one key with Status=Active (found 0)");
    }

    [Fact]
    public async Task Start_WeakRsaKeyAndDuplicateKid_ExitsWith1NamingEveryKey()
    {
        using var weak = RSA.Create(1024);

        var (exitCode, output) = await HostProcess.RunAsync(new Dictionary<string, string>
        {
            ["Auth__Jwt__SigningKeys__0__PrivateKeyBase64"] = TestSigningKeys.ToBase64Pem(weak),
            ["Auth__Jwt__SigningKeys__1__Kid"] = TestSigningKeys.DefaultKid,
            ["Auth__Jwt__SigningKeys__1__Status"] = "Next",
            ["Auth__Jwt__SigningKeys__1__PrivateKeyBase64"] = TestSigningKeys.CreatePrivateKeyBase64(),
        });

        exitCode.ShouldBe(1);
        output.ShouldContain("Auth:Jwt:SigningKeys:0:PrivateKeyBase64 holds a 1024-bit RSA key; at least 2048 bits are required.");
        output.ShouldContain("Auth:Jwt:SigningKeys:1:Kid 'test-active' is used by more than one key");
        output.ShouldNotContain("PRIVATE KEY");
    }
}
