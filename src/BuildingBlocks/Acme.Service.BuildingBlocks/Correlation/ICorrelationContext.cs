namespace Acme.Service.BuildingBlocks.Correlation;

/// <summary>
/// Ambient correlation id of the current logical operation (HTTP request, outbox dispatch, job), readable
/// and settable by infrastructure that must not depend on ASP.NET Core (outbox, jobs; D-25). The Host bridges
/// it to the web correlation accessor; when it is not registered, infrastructure falls back to the current
/// W3C trace id.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>The current correlation id, or <see langword="null"/> when none is in scope.</summary>
    string? CorrelationId { get; set; }
}
