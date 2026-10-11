namespace Acme.Service.BuildingBlocks.UseCases;

/// <summary>
/// Marks a use-case input as a read-only query. Queries never open a unit-of-work transaction and
/// are safe to retry on transient faults (D-28).
/// </summary>
/// <typeparam name="TOut">Success value type of the use case.</typeparam>
public interface IQuery<TOut>;
