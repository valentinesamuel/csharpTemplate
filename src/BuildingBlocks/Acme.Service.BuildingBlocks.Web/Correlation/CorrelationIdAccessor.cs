namespace Acme.Service.BuildingBlocks.Web.Correlation;

/// <summary><see cref="ICorrelationIdAccessor"/> backed by an <see cref="AsyncLocal{T}"/> so the value flows with the async call chain.</summary>
public sealed class CorrelationIdAccessor : ICorrelationIdAccessor
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <inheritdoc />
    public string? CorrelationId
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
