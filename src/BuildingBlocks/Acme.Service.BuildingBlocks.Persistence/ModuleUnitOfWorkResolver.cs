using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>Resolves the keyed scoped <see cref="IUnitOfWork"/> registered by a module (D-04).</summary>
internal sealed class ModuleUnitOfWorkResolver(IServiceProvider serviceProvider) : IModuleUnitOfWorkResolver
{
    /// <inheritdoc />
    public IUnitOfWork Resolve(string moduleKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);

        return serviceProvider.GetKeyedService<IUnitOfWork>(moduleKey)
            ?? throw new InvalidOperationException(
                $"No unit of work is registered for module '{moduleKey}'. Call AddModuleDbContext with this module key.");
    }
}
