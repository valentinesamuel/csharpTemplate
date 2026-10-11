using Acme.Service.BuildingBlocks.Paging;
using Acme.Service.BuildingBlocks.Results;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Paging;

public sealed class SortMapTests
{
    private static readonly SortMap<Row> _map = new SortMap<Row>()
        .Add("createdAt", r => r.CreatedAt)
        .Add("total", r => r.Total);

    [Fact]
    public void Resolve_DescendingAndAscendingFields_ReturnsOrderedExpressions()
    {
        var result = _map.Resolve("-createdAt,total");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(2);
        result.Value[0].Descending.ShouldBeTrue();
        result.Value[0].KeySelector.Compile()(new Row(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), 5m))
            .ShouldBe(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        result.Value[1].Descending.ShouldBeFalse();
        result.Value[1].KeySelector.Compile()(new Row(DateTimeOffset.UnixEpoch, 5m)).ShouldBe(5m);
    }

    [Fact]
    public void Resolve_UnknownField_ReturnsValidationError()
    {
        var result = _map.Resolve("-createdAt,password");

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Code.ShouldBe(SortSpec.InvalidSortCode);
        result.Error.Details.ShouldNotBeNull().ShouldContainKey(SortSpec.ParameterName);
    }

    [Theory]
    [InlineData("CreatedAt")]
    [InlineData("-")]
    [InlineData("total,")]
    [InlineData("total,-total")]
    public void Resolve_MalformedOrWrongCaseSort_ReturnsValidationError(string sort)
    {
        _map.Resolve(sort).Error.Type.ShouldBe(ErrorType.Validation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_MissingSort_ReturnsEmptyList(string? sort)
    {
        _map.Resolve(sort).Value.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_TermsWithWhitespace_ReturnsTrimmedSpecs()
    {
        SortSpec.Parse(" -createdAt , total ").Value
            .ShouldBe([new SortSpec("createdAt", true), new SortSpec("total", false)]);
    }

    private sealed record Row(DateTimeOffset CreatedAt, decimal Total);
}
