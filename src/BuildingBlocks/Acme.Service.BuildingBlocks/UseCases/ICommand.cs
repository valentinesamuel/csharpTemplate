namespace Acme.Service.BuildingBlocks.UseCases;

/// <summary>
/// Marks a use-case input as a state-changing command. Commands run inside the owning module's
/// unit of work (transaction behaviour) and are not retried on transient faults unless they also
/// implement <see cref="IRetryableCommand"/> (D-28, INV-26).
/// </summary>
/// <typeparam name="TOut">Success value type of the use case.</typeparam>
public interface ICommand<TOut>;
