using Acme.Service.BuildingBlocks.Paging;
using Acme.Service.BuildingBlocks.Persistence;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Persistence;

public sealed class SpecificationTests
{
    [Fact]
    public void Where_CalledTwice_CombinesCriteriaWithAnd()
    {
        var spec = new Specification<Row>().Where(r => r.Total > 10).Where(r => r.Total < 20);
        var predicate = spec.Criteria.ShouldNotBeNull().Compile();

        predicate(new Row(15)).ShouldBeTrue();
        predicate(new Row(5)).ShouldBeFalse();
        predicate(new Row(25)).ShouldBeFalse();
    }

    [Fact]
    public void ApplyPaging_PageRequest_SetsSkipAndTake()
    {
        var spec = new Specification<Row>().ApplyPaging(new PageRequest(2, 30)).AddOrderBy(r => r.Total, descending: true);

        spec.Skip.ShouldBe(30);
        spec.Take.ShouldBe(30);
        spec.OrderBy.Single().Descending.ShouldBeTrue();
    }

    private sealed record Row(int Total);
}
