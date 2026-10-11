namespace Acme.Service.Modules.Orders;

/// <summary>
/// The module name: the schema, and the key of the module's keyed unit of work and outbox (D-04). A constant so that
/// use cases can name it in <c>[FromKeyedServices]</c> without depending on the persistence adapter.
/// </summary>
internal static class OrdersModuleKey
{
    public const string Value = "orders";
}
