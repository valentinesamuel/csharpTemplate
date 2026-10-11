using Acme.Service.BuildingBlocks.Web.Correlation;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Correlation;

public sealed class RequestIdTests
{
    private const string Alphanumeric = "^[A-Za-z0-9]+$";

    [Fact]
    public void Sanitize_KestrelTraceIdentifier_StripsTheColon() =>
        RequestId.Sanitize("0HNP7R1GJ7MSQ:00000002").ShouldBe("0HNP7R1GJ7MSQ00000002");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-_:.!")]
    public void Sanitize_EmptyOrGarbage_GeneratesAnAlphanumericId(string? input) =>
        RequestId.Sanitize(input).ShouldMatch(Alphanumeric);
}

public sealed class CorrelationIdValidationTests
{
    [Theory]
    [InlineData("abc123")]
    [InlineData("ABCdef789")]
    public void IsValid_Alphanumeric_IsAccepted(string value) => CorrelationId.IsValid(value).ShouldBeTrue();

    [Theory]
    [InlineData("corr-123")]
    [InlineData("corr_123")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_HyphenUnderscoreOrEmpty_IsRejected(string? value) => CorrelationId.IsValid(value).ShouldBeFalse();

    [Fact]
    public void IsValid_LongerThanMaxLength_IsRejected() =>
        CorrelationId.IsValid(new string('a', CorrelationId.MaxLength + 1)).ShouldBeFalse();
}
