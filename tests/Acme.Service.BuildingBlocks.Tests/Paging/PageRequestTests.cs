using Acme.Service.BuildingBlocks.Paging;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Paging;

public sealed class PageRequestTests
{
    [Fact]
    public void Constructor_NoValues_UsesFirstPageAndDefaultSize()
    {
        var request = new PageRequest();

        request.Page.ShouldBe(1);
        request.PageSize.ShouldBe(20);
        request.Skip.ShouldBe(0);
    }

    [Theory]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(100, 100)]
    [InlineData(50, 50)]
    [InlineData(0, 20)]
    [InlineData(-5, 20)]
    public void Constructor_PageSize_IsClampedToRange(int pageSize, int expected)
    {
        new PageRequest(1, pageSize).PageSize.ShouldBe(expected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(3, 3)]
    public void Constructor_Page_IsAtLeastOne(int page, int expected)
    {
        new PageRequest(page, 10).Page.ShouldBe(expected);
    }

    [Fact]
    public void Skip_ThirdPage_SkipsTwoPages()
    {
        new PageRequest(3, 25).Skip.ShouldBe(50);
    }

    [Fact]
    public void Skip_HugePage_DoesNotOverflow()
    {
        new PageRequest(int.MaxValue, 100).Skip.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    public void PagedResult_TotalCount_ComputesTotalPages(int totalCount, int expectedPages)
    {
        var paged = new PagedResult<int>([], new PageRequest(1, 20), totalCount);

        paged.TotalPages.ShouldBe(expectedPages);
        paged.Page.ShouldBe(1);
        paged.PageSize.ShouldBe(20);
    }
}
