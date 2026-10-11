namespace Acme.Service.Modules.Orders.Domain;

/// <summary>
/// Lifecycle of an <see cref="Order"/>. Only a <see cref="Pending"/> order changes state: to <see cref="Confirmed"/>
/// (staff), <see cref="Cancelled"/> (the customer or staff) or <see cref="Expired"/> (the expiry job, P17). Stored as
/// its name.
/// </summary>
internal enum OrderStatus
{
    /// <summary>Placed and awaiting confirmation; the lines may still change.</summary>
    Pending,

    /// <summary>Confirmed by staff. Final.</summary>
    Confirmed,

    /// <summary>Cancelled by the customer or staff. Final.</summary>
    Cancelled,

    /// <summary>Left pending too long and expired. Final.</summary>
    Expired,
}
