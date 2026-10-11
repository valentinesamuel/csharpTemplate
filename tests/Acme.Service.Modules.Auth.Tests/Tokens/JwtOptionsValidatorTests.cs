using System.Security.Cryptography;
using System.Text;
using Acme.Service.Modules.Auth.Tokens;
using Acme.Service.Testing.Security;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Tokens;

public sealed class JwtOptionsValidatorTests
{
    private static readonly string _keyA = TestSigningKeys.DefaultPrivateKeyBase64;
    private static readonly Lazy<string> _keyB = new(() => TestSigningKeys.CreatePrivateKeyBase64());

    [Fact]
    public void Validate_OneActiveKey_Succeeds()
    {
        var result = Validate(Options(Key("a", "Active", _keyA)));

        result.Succeeded.ShouldBeTrue(result.FailureMessage);
    }

    [Fact]
    public void Validate_ActiveNextAndRetiring_Succeeds()
    {
        var result = Validate(Options(Key("a", "Active", _keyA), Key("b", "next", _keyB.Value), Key("c", "Retiring", _keyA)));

        result.Succeeded.ShouldBeTrue(result.FailureMessage);
    }

    [Fact]
    public void Validate_NoKeys_FailsNamingKey()
    {
        var result = Validate(Options());

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys must contain exactly one key with Status=Active (found 0)");
    }

    [Fact]
    public void Validate_NoActiveKey_Fails()
    {
        var result = Validate(Options(Key("a", "Next", _keyA)));

        result.FailureMessage.ShouldContain("exactly one key with Status=Active (found 0)");
    }

    [Fact]
    public void Validate_TwoActiveKeys_Fails()
    {
        var result = Validate(Options(Key("a", "Active", _keyA), Key("b", "Active", _keyB.Value)));

        result.FailureMessage.ShouldContain("exactly one key with Status=Active (found 2)");
    }

    [Fact]
    public void Validate_DuplicateKid_FailsNamingKey()
    {
        var result = Validate(Options(Key("a", "Active", _keyA), Key("a", "Next", _keyB.Value)));

        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys:1:Kid 'a' is used by more than one key");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123x")]
    public void Validate_InvalidKid_FailsNamingKey(string? kid)
    {
        var result = Validate(Options(Key(kid, "Active", _keyA)));

        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys:0:Kid is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Primary")]
    [InlineData("0")]
    [InlineData("Active,Next")]
    public void Validate_InvalidStatus_FailsNamingKey(string? status)
    {
        var result = Validate(Options(Key("a", status, _keyA)));

        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys:0:Status must be one of Active, Next or Retiring.");
    }

    [Fact]
    public void Validate_RsaKeyBelow2048Bits_FailsNamingKey()
    {
        using var small = RSA.Create(1024);

        var result = Validate(Options(Key("a", "Active", TestSigningKeys.ToBase64Pem(small))));

        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys:0:PrivateKeyBase64 holds a 1024-bit RSA key; at least 2048 bits are required.");
    }

    [Fact]
    public void Validate_BothPathAndBase64_FailsNamingKey()
    {
        var key = Key("a", "Active", _keyA);
        key.PrivateKeyPath = "/run/secrets/jwt.pem";

        var result = Validate(Options(key));

        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys:0 must set exactly one of PrivateKeyPath or PrivateKeyBase64.");
    }

    [Fact]
    public void Validate_NeitherPathNorBase64_FailsNamingKey()
    {
        var result = Validate(Options(Key("a", "Active", null)));

        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys:0 must set exactly one of PrivateKeyPath or PrivateKeyBase64.");
    }

    [Fact]
    public void Validate_InvalidBase64_FailsWithoutEchoingValue()
    {
        const string value = "not-base64-secret-value!";

        var result = Validate(Options(Key("a", "Active", value)));

        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys:0:PrivateKeyBase64 is not valid base64");
        result.FailureMessage.ShouldNotContain(value);
    }

    [Fact]
    public void Validate_PublicKeyPem_FailsAsNotPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var publicPem = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem()));

        var result = Validate(Options(Key("a", "Active", publicPem)));

        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys:0:PrivateKeyBase64 does not hold an RSA private key in PEM format.");
    }

    [Fact]
    public void Validate_NonRsaPem_Fails()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecPem = Convert.ToBase64String(Encoding.UTF8.GetBytes(ec.ExportPkcs8PrivateKeyPem()));

        var result = Validate(Options(Key("a", "Active", ecPem)));

        result.FailureMessage.ShouldContain("does not hold an RSA private key in PEM format.");
    }

    [Fact]
    public void Validate_PrivateKeyPathToPemFile_Succeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jwt-{Guid.NewGuid():N}.pem");
        File.WriteAllBytes(path, Convert.FromBase64String(_keyA));
        try
        {
            var result = Validate(Options(new SigningKeyOptions { Kid = "file", Status = "Active", PrivateKeyPath = path }));

            result.Succeeded.ShouldBeTrue(result.FailureMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_PrivateKeyPathMissing_FailsNamingKey()
    {
        var result = Validate(Options(new SigningKeyOptions { Kid = "file", Status = "Active", PrivateKeyPath = "/nonexistent/jwt.pem" }));

        result.FailureMessage.ShouldContain("Auth:Jwt:SigningKeys:0:PrivateKeyPath could not be read.");
    }

    [Fact]
    public void Validate_MissingIssuerAudienceAndBadLifetimes_ReportsEveryKey()
    {
        var options = Options(Key("a", "Active", _keyA));
        options.Issuer = " ";
        options.Audience = null;
        options.AccessTokenLifetime = TimeSpan.Zero;
        options.RefreshTokenLifetime = TimeSpan.FromSeconds(-1);
        options.SessionAbsoluteLifetime = TimeSpan.Zero;
        options.ClockSkew = TimeSpan.FromMinutes(6);

        var message = Validate(options).FailureMessage;

        message.ShouldContain("Auth:Jwt:Issuer is required.");
        message.ShouldContain("Auth:Jwt:Audience is required.");
        message.ShouldContain("Auth:Jwt:AccessTokenLifetime must be positive.");
        message.ShouldContain("Auth:Jwt:RefreshTokenLifetime must be positive.");
        message.ShouldContain("Auth:Jwt:SessionAbsoluteLifetime must be positive.");
        message.ShouldContain("Auth:Jwt:ClockSkew must be between 00:00:00 and 00:05:00.");
    }

    [Fact]
    public void Validate_AnyFailure_NeverContainsKeyMaterial()
    {
        var result = Validate(Options(Key("a", "Active", _keyA), Key("b", "Active", _keyA)));

        result.FailureMessage.ShouldNotContain(_keyA[..40]);
        result.FailureMessage.ShouldNotContain("PRIVATE KEY");
    }

    private static Outcome Validate(JwtOptions options)
    {
        var result = new JwtOptionsValidator().Validate(null, options);
        return new Outcome(result.Succeeded, result.Failed, result.FailureMessage ?? string.Empty);
    }

    private sealed record Outcome(bool Succeeded, bool Failed, string FailureMessage);

    private static SigningKeyOptions Key(string? kid, string? status, string? base64) =>
        new() { Kid = kid, Status = status, PrivateKeyBase64 = base64 };

    private static JwtOptions Options(params SigningKeyOptions[] keys)
    {
        var options = new JwtOptions { Issuer = "acme-service", Audience = "acme-service" };
        foreach (var key in keys)
        {
            options.SigningKeys.Add(key);
        }

        return options;
    }
}
