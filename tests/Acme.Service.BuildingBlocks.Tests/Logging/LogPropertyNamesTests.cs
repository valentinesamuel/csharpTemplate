using Acme.Service.BuildingBlocks.Logging;
using Acme.Service.BuildingBlocks.Web.Correlation;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Logging;

public sealed class LogPropertyNamesTests
{
    public static TheoryData<string> AlwaysReservedNames => new(
        "CorrelationId",
        "RequestId",
        "SourceContext",
        "EventId",
        "TraceId",
        "SpanId",
        "ParentId",
        "RequestPath",
        "ConnectionId",
        "ActionId",
        "ActionName",
        "Scope",
        "{OriginalFormat}",
        "Http.Method",
        "Http.Path",
        "Http.StatusCode",
        "Http.DurationMs",
        "Http.IsSuccess");

    public static TheoryData<string> SummaryOnlyReservedNames => new(
        "RequestMethod",
        "StatusCode",
        "Elapsed");

    [Theory]
    [MemberData(nameof(AlwaysReservedNames))]
    public void AlwaysReserved_SpecName_IsInAlwaysTierOnly(string name)
    {
        LogPropertyNames.AlwaysReserved.ShouldContain(name);
        LogPropertyNames.SummaryOnlyReserved.ShouldNotContain(name);
    }

    [Theory]
    [MemberData(nameof(SummaryOnlyReservedNames))]
    public void SummaryOnlyReserved_SpecName_IsInSummaryTierOnly(string name)
    {
        LogPropertyNames.SummaryOnlyReserved.ShouldContain(name);
        LogPropertyNames.AlwaysReserved.ShouldNotContain(name);
    }

    [Fact]
    public void ReservedSets_ContainExactlyTheSpecNames()
    {
        LogPropertyNames.AlwaysReserved.Count.ShouldBe(AlwaysReservedNames.Count);
        LogPropertyNames.SummaryOnlyReserved.Count.ShouldBe(SummaryOnlyReservedNames.Count);
        LogPropertyNames.AlwaysReserved.Overlaps(LogPropertyNames.SummaryOnlyReserved).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Method")]
    [InlineData("Path")]
    [InlineData("RoutePattern")]
    [InlineData("UseCase")]
    [InlineData("ElapsedMs")]
    [InlineData("EventType")]
    [InlineData("MessageId")]
    [InlineData("JobName")]
    [InlineData("JobId")]
    [InlineData("HangfireMessage")]
    public void CallSiteName_IsInNeitherTier(string name)
    {
        LogPropertyNames.AlwaysReserved.ShouldNotContain(name);
        LogPropertyNames.SummaryOnlyReserved.ShouldNotContain(name);
    }

    [Theory]
    [InlineData("correlationId")]
    [InlineData("CORRELATIONID")]
    [InlineData("http.method")]
    public void ReservedSets_UseOrdinalComparison(string name)
    {
        LogPropertyNames.AlwaysReserved.ShouldNotContain(name);
    }

    [Theory]
    [InlineData(LogPropertyNames.HttpMethod)]
    [InlineData(LogPropertyNames.HttpPath)]
    [InlineData(LogPropertyNames.HttpStatusCode)]
    [InlineData(LogPropertyNames.HttpDurationMs)]
    [InlineData(LogPropertyNames.HttpIsSuccess)]
    public void HttpConstant_IsDotted(string name)
    {
        name.ShouldStartWith("Http.", Case.Sensitive);
        name.ShouldContain('.');
    }

    [Fact]
    public void ReservedSets_EveryHttpPrefixedName_IsDotted()
    {
        foreach (var name in LogPropertyNames.AlwaysReserved.Concat(LogPropertyNames.SummaryOnlyReserved)
                     .Where(n => n.StartsWith("Http", StringComparison.Ordinal)))
        {
            name.ShouldContain('.');
        }
    }

    [Fact]
    public void WebCorrelationIdPropertyName_IsTheSharedConstant()
    {
        CorrelationId.PropertyName.ShouldBe(LogPropertyNames.CorrelationId);
    }
}
