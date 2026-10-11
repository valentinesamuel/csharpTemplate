using Acme.Service.BuildingBlocks.Paging;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Web.Paging;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Web;

public sealed class ListQueryBindingTests
{
    private static readonly SortMap<Row> _sortMap = new SortMap<Row>()
        .Add("createdAt", r => r.CreatedAt)
        .Add("total", r => r.Total);

    [Fact]
    public void BindListQuery_NoParameters_ReturnsDefaults()
    {
        var result = Request(string.Empty).BindListQuery(_sortMap);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Page.ShouldBe(PageRequest.Default);
        result.Value.Sort.ShouldBeEmpty();
    }

    [Fact]
    public void BindListQuery_ValidParameters_BindsPageSizeAndSort()
    {
        var result = Request("?page=3&pageSize=50&sort=-createdAt,total").BindListQuery(_sortMap);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Page.Page.ShouldBe(3);
        result.Value.Page.PageSize.ShouldBe(50);
        result.Value.Sort.Count.ShouldBe(2);
        result.Value.Sort[0].Descending.ShouldBeTrue();
        result.Value.Sort[1].Descending.ShouldBeFalse();
    }

    [Fact]
    public void BindListQuery_OutOfRangeNumbers_AreClamped()
    {
        var result = Request("?page=0&pageSize=1000").BindListQuery(_sortMap);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Page.Page.ShouldBe(1);
        result.Value.Page.PageSize.ShouldBe(PageRequest.MaxPageSize);
    }

    [Theory]
    [InlineData("?page=abc", "page")]
    [InlineData("?pageSize=1.5", "pageSize")]
    [InlineData("?page=99999999999", "page")]
    [InlineData("?page=1&page=2", "page")]
    public void BindListQuery_MalformedPaging_ReturnsInvalidPaging(string query, string field)
    {
        var result = Request(query).BindListQuery(_sortMap);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Code.ShouldBe(ListQueryBinding.InvalidPagingCode);
        result.Error.Details.ShouldNotBeNull().ShouldContainKey(field);
    }

    [Theory]
    [InlineData("?sort=password")]
    [InlineData("?sort=-")]
    [InlineData("?sort=total&sort=createdAt")]
    public void BindListQuery_InvalidSort_ReturnsInvalidSort(string query)
    {
        var result = Request(query).BindListQuery(_sortMap);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Code.ShouldBe(SortSpec.InvalidSortCode);
    }

    private static HttpRequest Request(string queryString)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(queryString.Length == 0 ? null : queryString);
        return http.Request;
    }

    private sealed record Row(DateTimeOffset CreatedAt, decimal Total);
}
