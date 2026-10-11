namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>
/// Resolves the keyed scoped <see cref="IUnitOfWork"/> of a module (D-04). Implemented by
/// BuildingBlocks.Persistence.
/// </summary>
public interface IModuleUnitOfWorkResolver
{
    /// <summary>Returns the unit of work registered for <paramref name="moduleKey"/> in the current scope.</summary>
    /// <exception cref="InvalidOperationException">No unit of work is registered for the key.</exception>
    IUnitOfWork Resolve(string moduleKey);
}
