using Acme.Service.BuildingBlocks.Paging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Persistence;

[Collection(PostgresTestGroup.Name)]
public sealed class EfRepositoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AddAsync_ThenCommit_GetByIdAsyncReturnsEntity()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        var widget = new Widget { Name = "added", Quantity = 4 };

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredKeyedService<IUnitOfWork>(WidgetsDbContext.ModuleKey);
            var result = await unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    await repository.AddAsync(widget, ct);
                    return Result.Success();
                },
                retryOnTransientFailure: false,
                Ct);
            result.IsSuccess.ShouldBeTrue();
        }

        await using var readScope = host.Services.CreateAsyncScope();
        var loaded = await readScope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>().GetByIdAsync(widget.Id, Ct);
        loaded.ShouldNotBeNull().Quantity.ShouldBe(4);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Remove_ThenSave_DeletesEntity()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        var seeded = await host.SeedAsync("gone", Ct);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
            var widget = (await repository.GetByIdAsync(seeded.Id, Ct)).ShouldNotBeNull();
            repository.Remove(widget);
            await scope.ServiceProvider.GetRequiredKeyedService<IUnitOfWork>(WidgetsDbContext.ModuleKey).SaveChangesAsync(Ct);
        }

        (await host.ReadAllAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_CriteriaSortAndPaging_ReturnsRequestedPage()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            await host.SeedAsync(name, Ct);
        }

        await using var scope = host.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var spec = new Specification<Widget>()
            .Where(w => w.Name != "e")
            .AddOrderBy(w => w.Quantity)
            .AddOrderBy(w => w.Name, descending: true)
            .ApplyPaging(new PageRequest(2, 2));

        var page = await repository.ListAsync(spec, Ct);
        var count = await repository.CountAsync(spec, Ct);

        page.Select(w => w.Name).ShouldBe(["b", "a"]);
        count.ShouldBe(4);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_ConnectionsTerminated_RetriesQuery()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        await host.SeedAsync("survivor", Ct);
        await postgres.TerminateConnectionsAsync(host.ConnectionString, Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();

        var widgets = await repository.ListAsync(new Specification<Widget>(), Ct);

        widgets.Select(w => w.Name).ShouldBe(["survivor"]);
    }
}
