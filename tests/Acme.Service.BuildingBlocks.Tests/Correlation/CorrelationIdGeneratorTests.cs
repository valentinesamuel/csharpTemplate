using System.Diagnostics;
using Acme.Service.BuildingBlocks.Correlation;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Correlation;

public sealed class CorrelationIdGeneratorTests
{
    private const string LowercaseHex32 = "^[a-f0-9]{32}$";

    [Fact]
    public void Generate_WithoutAmbientActivity_Returns32LowercaseHex()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var first = CorrelationIdGenerator.Generate();
            var second = CorrelationIdGenerator.Generate();

            first.ShouldMatch(LowercaseHex32);
            second.ShouldMatch(LowercaseHex32);
            first.ShouldNotBe(second);
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public void Generate_WithAmbientActivity_ReturnsItsTraceId()
    {
        using var activity = new Activity("correlation-id-generator-test")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        var id = CorrelationIdGenerator.Generate();

        id.ShouldMatch(LowercaseHex32);
        id.ShouldBe(activity.TraceId.ToHexString());
    }
}
