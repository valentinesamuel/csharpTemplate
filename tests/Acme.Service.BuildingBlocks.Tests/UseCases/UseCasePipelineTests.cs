using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Tests.UseCases.Fixtures;
using Acme.Service.BuildingBlocks.UseCases;
using NSubstitute;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.UseCases;

public sealed class UseCasePipelineTests
{
    [Fact]
    public async Task ExecuteAsync_ValidCommand_RunsLoggingThenValidationThenTransactionThenUseCase()
    {
        await using var harness = new PipelineHarness();

        var result = await harness.Executor<CreateThingCommand, Guid>()
            .ExecuteAsync(new CreateThingCommand("box"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(CreateThingUseCase.CreatedId);
        harness.Trace.Entries.ShouldBe(
        [
            "log:Executing",
            "validator",
            "uow:begin",
            "use-case",
            "uow:commit",
            "log:Succeeded",
        ]);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyName_ShortCircuitsWithValidationErrorBeforeUseCase()
    {
        await using var harness = new PipelineHarness();

        var result = await harness.Executor<CreateThingCommand, Guid>()
            .ExecuteAsync(new CreateThingCommand(string.Empty), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Code.ShouldBe("validation_failed");
        result.Error.Details.ShouldNotBeNull().ShouldContainKey(nameof(CreateThingCommand.Name));
        harness.Trace.Entries.ShouldNotContain("use-case");
        harness.Trace.Entries.ShouldNotContain("uow:begin");
        harness.Resolver.DidNotReceiveWithAnyArgs().Resolve(default!);
    }

    [Fact]
    public async Task ExecuteAsync_AsyncRuleFails_ShortCircuitsWithValidationError()
    {
        await using var harness = new PipelineHarness();

        var result = await harness.Executor<CreateThingCommand, Guid>()
            .ExecuteAsync(new CreateThingCommand("forbidden"), TestContext.Current.CancellationToken);

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Details.ShouldNotBeNull()[nameof(CreateThingCommand.Name)].ShouldBe(["Name is not allowed."]);
        harness.Trace.Entries.ShouldBe(["log:Executing", "validator", "log:Failed"]);
    }

    [Fact]
    public async Task ExecuteAsync_UseCaseReturnsFailure_RollsBackAndReturnsError()
    {
        await using var harness = new PipelineHarness();

        var result = await harness.Executor<CreateThingCommand, Guid>()
            .ExecuteAsync(new CreateThingCommand("taken"), TestContext.Current.CancellationToken);

        result.Error.Type.ShouldBe(ErrorType.Conflict);
        result.Error.Code.ShouldBe("thing_exists");
        harness.Trace.Entries.ShouldBe(["log:Executing", "validator", "uow:begin", "use-case", "uow:rollback", "log:Failed"]);
    }

    [Fact]
    public async Task ExecuteAsync_UseCaseThrows_ExceptionPropagatesUnchanged()
    {
        await using var harness = new PipelineHarness();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            harness.Executor<ExplodeCommand, int>().ExecuteAsync(new ExplodeCommand(), TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("boom");
        harness.Trace.Entries.ShouldNotContain("uow:commit");
    }

    [Fact]
    public async Task ExecuteAsync_ManualBehaviors_FirstRegisteredIsOutermost()
    {
        var trace = new TraceLog();
        var pipeline = new UseCasePipeline<GetThingQuery, string>(
            new GetThingUseCase(trace),
            [new RecordingBehavior("a", trace), new RecordingBehavior("b", trace), new RecordingBehavior("c", trace)]);

        var result = await pipeline.ExecuteAsync(new GetThingQuery("x"), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("x");
        trace.Entries.ShouldBe(["a:in", "b:in", "c:in", "use-case", "c:out", "b:out", "a:out"]);
    }

    private sealed class RecordingBehavior(string name, TraceLog trace) : IUseCaseBehavior<GetThingQuery, string>
    {
        public async Task<Result<string>> HandleAsync(GetThingQuery input, UseCaseNext<string> continuation, CancellationToken cancellationToken)
        {
            trace.Add($"{name}:in");
            var result = await continuation(cancellationToken);
            trace.Add($"{name}:out");
            return result;
        }
    }
}
