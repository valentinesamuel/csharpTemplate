namespace Acme.Service.Modules.Orders.Domain;

/// <summary>
/// The Orders module's local copy of a customer's contact details, fed by Auth's <c>UserRegisteredV1</c> (INV-06: no
/// cross-schema joins; the read model lives in <c>orders.customer_snapshots</c>). <see cref="Id"/> is the user id.
/// </summary>
internal sealed class CustomerSnapshot
{
    /// <summary>Longest stored email address.</summary>
    public const int EmailMaxLength = 256;

    /// <summary>Longest stored display name.</summary>
    public const int DisplayNameMaxLength = 256;

    private CustomerSnapshot(Guid id, string email, string? displayName, DateTimeOffset updatedAt)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        UpdatedAt = updatedAt;
    }

    // For the persistence mapping.
    private CustomerSnapshot()
    {
        Email = string.Empty;
    }

    /// <summary>The customer's user id (<c>sub</c>).</summary>
    public Guid Id { get; private set; }

    /// <summary>The customer's email address.</summary>
    public string Email { get; private set; }

    /// <summary>The display name, when the user set one.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>When the source data last changed (UTC): the <c>OccurredAt</c> of the event applied last.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates the snapshot of user <paramref name="id"/> as of <paramref name="occurredAt"/>.</summary>
    /// <exception cref="ArgumentException">The id or email is missing or too long.</exception>
    public static CustomerSnapshot Create(Guid id, string email, string? displayName, DateTimeOffset occurredAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The customer id is required.", nameof(id));
        }

        var snapshot = new CustomerSnapshot(id, string.Empty, null, occurredAt);
        snapshot.Set(email, displayName);
        return snapshot;
    }

    /// <summary>
    /// Applies details that changed at <paramref name="occurredAt"/>. Details older than the stored ones are ignored, so
    /// events delivered out of order never overwrite newer data. Returns true when the snapshot changed.
    /// </summary>
    public bool Apply(string email, string? displayName, DateTimeOffset occurredAt)
    {
        if (occurredAt < UpdatedAt)
        {
            return false;
        }

        Set(email, displayName);
        UpdatedAt = occurredAt;
        return true;
    }

    private void Set(string email, string? displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (email.Length > EmailMaxLength)
        {
            throw new ArgumentException($"An email address is at most {EmailMaxLength} characters.", nameof(email));
        }

        Email = email;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : Truncate(displayName.Trim(), DisplayNameMaxLength);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
