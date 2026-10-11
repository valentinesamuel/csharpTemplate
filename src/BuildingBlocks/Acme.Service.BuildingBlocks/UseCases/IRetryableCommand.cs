namespace Acme.Service.BuildingBlocks.UseCases;

/// <summary>
/// Opt-in marker for an idempotent <see cref="ICommand{TOut}"/> whose transaction may be retried
/// automatically on transient faults (D-28, INV-26). Commands without it surface a transient fault
/// as 503 + Retry-After instead.
/// </summary>
public interface IRetryableCommand;
