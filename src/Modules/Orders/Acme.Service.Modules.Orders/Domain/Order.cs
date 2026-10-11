using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.Modules.Orders.Domain;

/// <summary>
/// An order placed by a customer (the <c>sub</c> of the caller), in one ISO 4217 currency, with 1 to
/// <see cref="MaxLines"/> lines. <see cref="Total"/> is recomputed from the lines whenever they change.
/// </summary>
/// <remarks>
/// Transitions: <see cref="OrderStatus.Pending"/> to <see cref="OrderStatus.Confirmed"/>, <see cref="OrderStatus.Cancelled"/>
/// or <see cref="OrderStatus.Expired"/>; the lines change only while pending. Any other change fails with
/// <see cref="InvalidTransition"/> (409). Deletion is soft (<see cref="DeletedAt"/>); deleted orders are invisible to
/// every query. <see cref="Version"/> is the optimistic concurrency token, assigned by the store on every write.
/// </remarks>
internal sealed class Order
{
    /// <summary>Most lines an order may have.</summary>
    public const int MaxLines = 100;

    /// <summary>Length of an ISO 4217 currency code.</summary>
    public const int CurrencyLength = 3;

    /// <summary>The change is not allowed in the order's current status (409).</summary>
    public static readonly Error InvalidTransition = Error.Conflict(
        "invalid_transition", "The order cannot be changed in its current status.");

    private readonly List<OrderLine> _lines = [];

    private Order(Guid id, Guid customerId, string currency, DateTimeOffset now)
    {
        Id = id;
        CustomerId = customerId;
        Currency = currency;
        Status = OrderStatus.Pending;
        CreatedAt = now;
        UpdatedAt = now;
    }

    // For the persistence mapping.
    private Order()
    {
        Currency = string.Empty;
    }

    /// <summary>Order id (Guid v7).</summary>
    public Guid Id { get; private set; }

    /// <summary>The customer: the user id (<c>sub</c>) of whoever placed the order.</summary>
    public Guid CustomerId { get; private set; }

    /// <summary>Current status.</summary>
    public OrderStatus Status { get; private set; }

    /// <summary>ISO 4217 currency code (three upper-case letters) of every price in the order.</summary>
    public string Currency { get; private set; }

    /// <summary>Sum of the line totals.</summary>
    public decimal Total { get; private set; }

    /// <summary>When the order was placed (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the order last changed (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>When the order was deleted (UTC), or null.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Optimistic concurrency token; changes on every write.</summary>
    public uint Version { get; private set; }

    /// <summary>The lines, in submission order.</summary>
    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>Places a pending order for <paramref name="customerId"/>.</summary>
    /// <exception cref="ArgumentException">The currency or a line is invalid, or there are no or too many lines.</exception>
    public static Order Place(Guid customerId, string currency, IReadOnlyList<OrderLineDraft> lines, DateTimeOffset now)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("The customer is required.", nameof(customerId));
        }

        if (!IsValidCurrency(currency))
        {
            throw new ArgumentException("The currency must be an ISO 4217 code of three upper-case letters.", nameof(currency));
        }

        var order = new Order(Guid.CreateVersion7(), customerId, currency, now);
        order.SetLines(lines);
        return order;
    }

    /// <summary>True for three upper-case ASCII letters (the ISO 4217 code format).</summary>
    public static bool IsValidCurrency(string? currency) =>
        currency is { Length: CurrencyLength } && currency.All(char.IsAsciiLetterUpper);

    /// <summary>True when <paramref name="customerId"/> placed the order.</summary>
    public bool IsOwnedBy(Guid customerId) => CustomerId == customerId;

    /// <summary>Replaces every line. Pending orders only.</summary>
    public Result ReplaceLines(IReadOnlyList<OrderLineDraft> lines, DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending)
        {
            return InvalidTransition;
        }

        SetLines(lines);
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Pending to Confirmed.</summary>
    public Result Confirm(DateTimeOffset now) => MoveTo(OrderStatus.Confirmed, now);

    /// <summary>Pending to Cancelled.</summary>
    public Result Cancel(DateTimeOffset now) => MoveTo(OrderStatus.Cancelled, now);

    /// <summary>Pending to Expired.</summary>
    public Result Expire(DateTimeOffset now) => MoveTo(OrderStatus.Expired, now);

    /// <summary>Soft-deletes the order, in any status. Deleting twice keeps the first time.</summary>
    public void Delete(DateTimeOffset now)
    {
        if (DeletedAt is null)
        {
            DeletedAt = now;
            UpdatedAt = now;
        }
    }

    private Result MoveTo(OrderStatus target, DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending)
        {
            return InvalidTransition;
        }

        Status = target;
        UpdatedAt = now;
        return Result.Success();
    }

    private void SetLines(IReadOnlyList<OrderLineDraft> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count is 0 or > MaxLines)
        {
            throw new ArgumentException($"An order has 1 to {MaxLines} lines.", nameof(lines));
        }

        var replacement = lines.Select((draft, index) => new OrderLine(index + 1, draft)).ToList();
        _lines.Clear();
        _lines.AddRange(replacement);
        Total = _lines.Sum(l => l.LineTotal);
    }
}
