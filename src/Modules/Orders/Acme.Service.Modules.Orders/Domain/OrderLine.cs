namespace Acme.Service.Modules.Orders.Domain;

/// <summary>
/// One line of an <see cref="Order"/>: a quantity of a SKU at a unit price in the order's currency. Lines belong to their
/// order and are replaced as a whole, never edited one by one.
/// </summary>
internal sealed class OrderLine
{
    /// <summary>Longest accepted SKU.</summary>
    public const int SkuMaxLength = 64;

    /// <summary>Largest accepted quantity of one line.</summary>
    public const int MaxQuantity = 10_000;

    /// <summary>Largest accepted unit price. With <see cref="MaxQuantity"/> and <see cref="Order.MaxLines"/> it keeps every total within numeric(18,2).</summary>
    public const decimal MaxUnitPrice = 1_000_000_000m;

    /// <summary>Decimal places of a unit price (numeric(18,2)).</summary>
    public const int PriceScale = 2;

    internal OrderLine(int lineNumber, OrderLineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentOutOfRangeException.ThrowIfLessThan(lineNumber, 1);
        if (!IsValidSku(draft.Sku))
        {
            throw new ArgumentException($"A SKU is 1 to {SkuMaxLength} characters without surrounding white space.", nameof(draft));
        }

        if (!IsValidQuantity(draft.Quantity))
        {
            throw new ArgumentOutOfRangeException(nameof(draft), draft.Quantity, $"A quantity is 1 to {MaxQuantity}.");
        }

        if (!IsValidUnitPrice(draft.UnitPrice))
        {
            throw new ArgumentOutOfRangeException(nameof(draft), draft.UnitPrice, $"A unit price is 0 to {MaxUnitPrice} with at most {PriceScale} decimals.");
        }

        Id = Guid.CreateVersion7();
        LineNumber = lineNumber;
        Sku = draft.Sku;
        Quantity = draft.Quantity;
        UnitPrice = draft.UnitPrice;
    }

    // For the persistence mapping.
    private OrderLine()
    {
        Sku = string.Empty;
    }

    /// <summary>Line id (Guid v7).</summary>
    public Guid Id { get; private set; }

    /// <summary>1-based position of the line in the order, as submitted.</summary>
    public int LineNumber { get; private set; }

    /// <summary>The stock keeping unit.</summary>
    public string Sku { get; private set; }

    /// <summary>Quantity, at least 1.</summary>
    public int Quantity { get; private set; }

    /// <summary>Unit price, at least 0, with at most two decimals.</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary><see cref="Quantity"/> times <see cref="UnitPrice"/>.</summary>
    public decimal LineTotal => Quantity * UnitPrice;

    /// <summary>True for a non-blank SKU of at most <see cref="SkuMaxLength"/> characters without surrounding white space.</summary>
    public static bool IsValidSku(string? sku) =>
        !string.IsNullOrWhiteSpace(sku) && sku.Length <= SkuMaxLength && sku.Trim().Length == sku.Length;

    /// <summary>True for 1 to <see cref="MaxQuantity"/>.</summary>
    public static bool IsValidQuantity(int quantity) => quantity is >= 1 and <= MaxQuantity;

    /// <summary>True for 0 to <see cref="MaxUnitPrice"/> with at most <see cref="PriceScale"/> decimals.</summary>
    public static bool IsValidUnitPrice(decimal unitPrice) =>
        unitPrice is >= 0m and <= MaxUnitPrice && decimal.Round(unitPrice, PriceScale) == unitPrice;
}

/// <summary>A line as requested by the caller, before it becomes part of an <see cref="Order"/>.</summary>
/// <param name="Sku">The stock keeping unit.</param>
/// <param name="Quantity">Quantity, at least 1.</param>
/// <param name="UnitPrice">Unit price, at least 0, with at most two decimals.</param>
internal sealed record OrderLineDraft(string Sku, int Quantity, decimal UnitPrice);
