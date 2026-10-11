using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Persistence;

[Collection(PostgresTestGroup.Name)]
public sealed class EfUnitOfWorkTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_SuccessfulResult_CommitsChanges()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);

        var result = await RunAsync(host, async (context, ct) =>
        {
            await context.Widgets.AddAsync(new Widget { Name = "alpha" }, ct);
            return Result.Success();
        });

        result.IsSuccess.ShouldBeTrue();
        (await host.ReadAllAsync(Ct)).Select(w => w.Name).ShouldBe(["alpha"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_FailedResult_RollsBackChanges()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        await host.SeedAsync("existing", Ct);

        var result = await RunAsync(host, async (context, ct) =>
        {
            await context.Widgets.AddAsync(new Widget { Name = "rejected" }, ct);
            var existing = await context.Widgets.SingleAsync(w => w.Name == "existing", ct);
            existing.Quantity = 99;
            await context.SaveChangesAsync(ct);
            return Error.Unprocessable("rule_broken", "No.");
        });

        result.Error.Code.ShouldBe("rule_broken");
        var widgets = await host.ReadAllAsync(Ct);
        widgets.Select(w => w.Name).ShouldBe(["existing"]);
        widgets[0].Quantity.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_OperationThrows_RollsBackAndRethrows()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);

        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(host, async (context, ct) =>
        {
            await context.Widgets.AddAsync(new Widget { Name = "doomed" }, ct);
            await context.SaveChangesAsync(ct);
            throw new InvalidOperationException("boom");
        }));

        (await host.ReadAllAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_StaleRowVersion_ReturnsConflict()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        var seeded = await host.SeedAsync("contested", Ct);

        await using var staleScope = host.Services.CreateAsyncScope();
        var staleContext = staleScope.ServiceProvider.GetRequiredService<WidgetsDbContext>();
        var stale = await staleContext.Widgets.SingleAsync(w => w.Id == seeded.Id, Ct);

        var first = await RunAsync(host, async (context, ct) =>
        {
            (await context.Widgets.SingleAsync(w => w.Id == seeded.Id, ct)).Quantity = 2;
            return Result.Success();
        });
        first.IsSuccess.ShouldBeTrue();

        var unitOfWork = staleScope.ServiceProvider.GetRequiredKeyedService<IUnitOfWork>(WidgetsDbContext.ModuleKey);
        var second = await unitOfWork.ExecuteInTransactionAsync(
            _ =>
            {
                stale.Quantity = 3;
                return Task.FromResult(Result.Success());
            },
            retryOnTransientFailure: false,
            Ct);

        second.Error.Type.ShouldBe(ErrorType.Conflict);
        second.Error.Code.ShouldBe("concurrency_conflict");
        (await host.ReadAllAsync(Ct)).Single().Quantity.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_DuplicateUniqueValue_ReturnsConflict()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        await host.SeedAsync("taken", Ct);

        var result = await RunAsync(host, async (context, ct) =>
        {
            await context.Widgets.AddAsync(new Widget { Name = "taken" }, ct);
            return Result.Success();
        });

        result.Error.Type.ShouldBe(ErrorType.Conflict);
        result.Error.Code.ShouldBe("unique_violation");
        (await host.ReadAllAsync(Ct)).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_TransientFaultNotRetryable_ReturnsUnavailableWithoutRetry()
    {
        var interceptor = new TransientFaultInterceptor { FailuresRemaining = 1 };
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct, AddInterceptor(interceptor));

        var result = await RunAsync(
            host,
            async (context, ct) =>
            {
                await context.Widgets.AddAsync(new Widget { Name = "once" }, ct);
                return Result.Success();
            },
            retryOnTransientFailure: false);

        result.Error.Type.ShouldBe(ErrorType.Unavailable);
        result.Error.Code.ShouldBe("database_unavailable");
        interceptor.WriteAttempts.ShouldBe(1);
        (await host.ReadAllAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_TransientFaultRetryable_RetriesAndCommitsOnce()
    {
        var interceptor = new TransientFaultInterceptor { FailuresRemaining = 1 };
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct, AddInterceptor(interceptor));
        var invocations = 0;

        var result = await RunAsync(
            host,
            async (context, ct) =>
            {
                invocations++;
                await context.Widgets.AddAsync(new Widget { Name = "retried" }, ct);
                return Result.Success();
            },
            retryOnTransientFailure: true);

        result.IsSuccess.ShouldBeTrue();
        invocations.ShouldBe(2);
        (await host.ReadAllAsync(Ct)).Select(w => w.Name).ShouldBe(["retried"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_TransientFaultPersists_ReturnsUnavailableAfterRetries()
    {
        var interceptor = new TransientFaultInterceptor { FailuresRemaining = int.MaxValue };
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct, AddInterceptor(interceptor));

        var result = await RunAsync(
            host,
            async (context, ct) =>
            {
                await context.Widgets.AddAsync(new Widget { Name = "never" }, ct);
                return Result.Success();
            },
            retryOnTransientFailure: true);

        result.Error.Type.ShouldBe(ErrorType.Unavailable);
        interceptor.WriteAttempts.ShouldBe(1 + 3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_ConnectionsTerminated_RetryableCommandSucceeds()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        await host.SeedAsync("warm-up", Ct);
        await postgres.TerminateConnectionsAsync(host.ConnectionString, Ct);

        var result = await RunAsync(
            host,
            async (context, ct) =>
            {
                await context.Widgets.AddAsync(new Widget { Name = "after-failover" }, ct);
                return Result.Success();
            },
            retryOnTransientFailure: true);

        result.IsSuccess.ShouldBeTrue();
        (await host.ReadAllAsync(Ct)).Select(w => w.Name).ShouldBe(["after-failover", "warm-up"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_ConnectionsTerminated_NonRetryableCommandReturnsUnavailable()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        await host.SeedAsync("warm-up", Ct);
        await postgres.TerminateConnectionsAsync(host.ConnectionString, Ct);

        var result = await RunAsync(
            host,
            async (context, ct) =>
            {
                await context.Widgets.AddAsync(new Widget { Name = "lost" }, ct);
                return Result.Success();
            },
            retryOnTransientFailure: false);

        result.Error.Type.ShouldBe(ErrorType.Unavailable);
        (await host.ReadAllAsync(Ct)).Select(w => w.Name).ShouldBe(["warm-up"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteInTransactionAsync_TransactionAlreadyOpen_JoinsOuterTransaction()
    {
        await using var host = await WidgetsHost.CreateAsync(postgres, Ct);
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredKeyedService<IUnitOfWork>(WidgetsDbContext.ModuleKey);

        var outer = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var inner = await unitOfWork.ExecuteInTransactionAsync(
                    async innerCt =>
                    {
                        await context.Widgets.AddAsync(new Widget { Name = "inner" }, innerCt);
                        return Result.Success();
                    },
                    retryOnTransientFailure: false,
                    ct);
                inner.IsSuccess.ShouldBeTrue();
                return Error.Conflict("outer_failed", "Outer failed.");
            },
            retryOnTransientFailure: false,
            Ct);

        outer.IsFailure.ShouldBeTrue();
        (await host.ReadAllAsync(Ct)).ShouldBeEmpty();
    }

    private static Action<IServiceCollection> AddInterceptor(TransientFaultInterceptor interceptor) =>
        services => services.ConfigureDbContext<WidgetsDbContext>(options => options.AddInterceptors(interceptor));

    private static async Task<Result> RunAsync(
        WidgetsHost host,
        Func<WidgetsDbContext, CancellationToken, Task<Result>> operation,
        bool retryOnTransientFailure = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IModuleUnitOfWorkResolver>().Resolve(WidgetsDbContext.ModuleKey);
        return await unitOfWork.ExecuteInTransactionAsync(ct => operation(context, ct), retryOnTransientFailure, Ct);
    }
}
