namespace Acme.Service.BuildingBlocks.Web.Correlation;

/// <summary>Exposes the correlation id of the current logical operation (HTTP request, job, consumer).</summary>
public interface ICorrelationIdAccessor
{
    /// <summary>The current correlation id, or <see langword="null"/> when none is in scope.</summary>
    string? CorrelationId { get; set; }
}
