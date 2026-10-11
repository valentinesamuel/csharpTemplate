using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Tests.UseCases.Fixtures;
using Acme.Service.BuildingBlocks.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.UseCases;

public sealed class TransactionBehaviorTests
{
    [Fact]
    public async Task HandleAsync_Query_DoesNotResolveOrOpenUnitOfWork()
    {
        await using var harness = new PipelineHarness();

        var result = await harness.Executor<GetThingQuery, string>()
            .ExecuteAsync(new GetThingQuery("x"), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("x");
        harness.Resolver.DidNotReceiveWithAnyArgs().Resolve(default!);
        await harness.UnitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, default, TestContext.Current.CancellationToken);
        harness.Trace.Entries.ShouldBe(["log:Executing", "use-case", "log:Succeeded"]);
    }

    [Fact]
    public async Task HandleAsync_Command_UsesModuleUnitOfWorkWithoutRetry()
    {
        await using var harness = new PipelineHarness();

        await harness.Executor<CreateThingCommand, Guid>()
            .ExecuteAsync(new CreateThingCommand("box"), TestContext.Current.CancellationToken);

        harness.Resolver.Received(1).Resolve(PipelineHarness.ModuleKey);
        await harness.UnitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<Result>>>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RetryableCommand_AllowsTransientRetry()
    {
        await using var harness = new PipelineHarness();

        var result = await harness.Executor<RenameThingCommand, string>()
            .ExecuteAsync(new RenameThingCommand("crate"), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("crate");
        await harness.UnitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<Result>>>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CommitFails_ReturnsUnitOfWorkError()
    {
        await using var harness = new PipelineHarness(
            _ => Task.FromResult(Result.Failure(Error.Unavailable("database_unavailable", "Try again later."))));

        var result = await harness.Executor<CreateThingCommand, Guid>()
            .ExecuteAsync(new CreateThingCommand("box"), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Unavailable);
        result.Error.Code.ShouldBe("database_unavailable");
    }

    [Fact]
    public async Task HandleAsync_QueryWithoutResolverRegistered_Succeeds()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TraceLog());
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        services.AddUseCasesFromAssembly(typeof(AssemblyMarker).Assembly, PipelineHarness.ModuleKey);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IUseCaseExecutor<GetThingQuery, string>>()
            .ExecuteAsync(new GetThingQuery("x"), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("x");
    }
}
