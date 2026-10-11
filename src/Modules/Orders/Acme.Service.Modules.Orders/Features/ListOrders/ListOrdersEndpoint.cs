using System.Globalization;
using Acme.Service.BuildingBlocks.Paging;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Authorization;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Paging;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Orders.Features.ListOrders;

/// <summary>
/// <c>GET /api/v1/orders?status=&amp;createdFrom=&amp;createdTo=&amp;page=&amp;pageSize=&amp;sort=</c> (<c>orders.read</c>):
/// the caller's orders, or every order with <c>orders.manage</c>, in the paging envelope. Sort fields: <c>createdAt</c>,
/// <c>total</c>, <c>status</c> (prefix <c>-</c> for descending); anything else is 400 <c>invalid_sort</c>. An unparsable
/// filter is 400 <c>invalid_filter</c>.
/// </summary>
internal sealed class ListOrdersEndpoint : IEndpoint
{
    public const string StatusParameter = "status";
    public const string CreatedFromParameter = "createdFrom";
    public const string CreatedToParameter = "createdTo";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(OrdersHttp.Prefix, async (HttpContext context, IUseCaseExecutor<ListOrdersQuery, PagedResult<OrderSummaryResponse>> executor) =>
            {
                if (!OrdersCaller.TryFrom(context.User, out var caller))
                {
                    return OrdersHttp.MissingClaims();
                }

                var list = context.Request.BindListQuery(ListOrdersSort.Map);
                if (list.IsFailure)
                {
                    return list.ToHttpResult();
                }

                var filters = BindFilters(context.Request.Query);
                if (filters.IsFailure)
                {
                    return filters.ToHttpResult();
                }

                var (status, createdFrom, createdTo) = filters.Value;
                var query = new ListOrdersQuery(caller, status, createdFrom, createdTo, list.Value.Page, list.Value.Sort);
                return (await executor.ExecuteAsync(query, context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .RequirePermission(OrdersPermissions.Read)
            .WithOrdersMetadata("ListOrders", "Lists orders, newest first unless sorted: the caller's own, or all with orders.manage.")
            .Produces<PagedResult<OrderSummaryResponse>>()
            .ProducesValidationProblem();
    }

    /// <summary>Parses the optional filters; a repeated or unparsable value is a validation failure.</summary>
    internal static Result<ListOrdersFilters> BindFilters(IQueryCollection query)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        OrderStatus? status = null;
        if (TryGetSingle(query, StatusParameter, errors, out var statusText) && statusText is not null)
        {
            status = ParseStatus(statusText);
            if (status is null)
            {
                errors[StatusParameter] = [$"'{StatusParameter}' must be one of {string.Join(", ", Enum.GetNames<OrderStatus>())}."];
            }
        }

        var createdFrom = ParseInstant(query, CreatedFromParameter, errors);
        var createdTo = ParseInstant(query, CreatedToParameter, errors);

        if (errors.Count > 0)
        {
            return Result.Failure<ListOrdersFilters>(
                Error.Validation(OrderErrors.InvalidFilterCode, "The list filters are invalid.", errors));
        }

        return new ListOrdersFilters(status, createdFrom, createdTo);
    }

    private static OrderStatus? ParseStatus(string text)
    {
        // Names only (case-insensitive); Enum.TryParse would also accept numbers.
        foreach (var candidate in Enum.GetValues<OrderStatus>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseInstant(IQueryCollection query, string name, Dictionary<string, string[]> errors)
    {
        if (!TryGetSingle(query, name, errors, out var text) || text is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
        {
            return value.ToUniversalTime();
        }

        errors[name] = [$"'{name}' must be an ISO 8601 date and time, for example 2026-01-31T00:00:00Z."];
        return null;
    }

    /// <summary>False with an error when repeated; true with null when absent or empty.</summary>
    private static bool TryGetSingle(IQueryCollection query, string name, Dictionary<string, string[]> errors, out string? value)
    {
        var values = query[name];
        if (values.Count > 1)
        {
            errors[name] = [$"'{name}' must be given at most once."];
            value = null;
            return false;
        }

        value = values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;
        return true;
    }
}

/// <summary>The parsed list filters.</summary>
/// <param name="Status">Status filter.</param>
/// <param name="CreatedFrom">Inclusive lower bound of the creation time (UTC).</param>
/// <param name="CreatedTo">Inclusive upper bound of the creation time (UTC).</param>
internal sealed record ListOrdersFilters(OrderStatus? Status, DateTimeOffset? CreatedFrom, DateTimeOffset? CreatedTo);
