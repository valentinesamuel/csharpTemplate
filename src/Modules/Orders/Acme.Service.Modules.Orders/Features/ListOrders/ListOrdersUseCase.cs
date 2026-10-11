using Acme.Service.BuildingBlocks.Paging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Domain;

namespace Acme.Service.Modules.Orders.Features.ListOrders;

/// <summary>
/// Lists orders (untracked) in the paging envelope. A caller without <c>orders.manage</c> sees only their own orders, so
/// the common query is served by the <c>(customer_id, created_at desc)</c> index. The id breaks sort ties so pages are
/// stable.
/// </summary>
internal sealed class ListOrdersUseCase(IOrderRepository orders) : IUseCase<ListOrdersQuery, PagedResult<OrderSummaryResponse>>
{
    public async Task<Result<PagedResult<OrderSummaryResponse>>> ExecuteAsync(ListOrdersQuery input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var specification = new Specification<Order>();
        if (!input.Caller.CanManage)
        {
            var customerId = input.Caller.UserId;
            specification.Where(o => o.CustomerId == customerId);
        }

        if (input.Status is { } status)
        {
            specification.Where(o => o.Status == status);
        }

        if (input.CreatedFrom is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            specification.Where(o => o.CreatedAt >= fromUtc);
        }

        if (input.CreatedTo is { } to)
        {
            var toUtc = to.ToUniversalTime();
            specification.Where(o => o.CreatedAt <= toUtc);
        }

        var totalCount = await orders.CountAsync(specification, cancellationToken).ConfigureAwait(false);

        if (input.Sort.Count == 0)
        {
            specification.AddOrderBy(o => o.CreatedAt, descending: true);
        }
        else
        {
            specification.AddOrderBy(input.Sort);
        }

        specification.AddOrderBy(o => o.Id, descending: true).ApplyPaging(input.Page);

        var page = await orders.ListForReadAsync(specification, cancellationToken).ConfigureAwait(false);
        return new PagedResult<OrderSummaryResponse>([.. page.Select(OrderSummaryResponse.From)], input.Page, totalCount);
    }
}
