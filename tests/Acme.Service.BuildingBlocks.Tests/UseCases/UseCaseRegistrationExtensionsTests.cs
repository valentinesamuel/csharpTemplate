using Acme.Service.BuildingBlocks.Tests.UseCases.Fixtures;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.UseCases.Behaviors;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.UseCases;

public sealed class UseCaseRegistrationExtensionsTests
{
    [Fact]
    public async Task AddUseCasesFromAssembly_ScannedAssembly_RegistersUseCaseValidatorAndExecutor()
    {
        await using var harness = new PipelineHarness();

        harness.Services.GetRequiredService<IUseCase<CreateThingCommand, Guid>>().ShouldBeOfType<CreateThingUseCase>();
        harness.Services.GetServices<IValidator<CreateThingCommand>>().Single().ShouldBeOfType<CreateThingValidator>();
        harness.Executor<CreateThingCommand, Guid>().ShouldBeOfType<UseCasePipeline<CreateThingCommand, Guid>>();
    }

    [Fact]
    public async Task AddUseCasesFromAssembly_Behaviors_ResolveInLoggingValidationTransactionOrder()
    {
        await using var harness = new PipelineHarness();

        var behaviors = harness.Services.GetServices<IUseCaseBehavior<CreateThingCommand, Guid>>().Select(b => b.GetType()).ToArray();

        behaviors.ShouldBe(
        [
            typeof(LoggingBehavior<CreateThingCommand, Guid>),
            typeof(ValidationBehavior<CreateThingCommand, Guid>),
            typeof(TransactionBehavior<CreateThingCommand, Guid>),
        ]);
    }

    [Fact]
    public void AddUseCasesFromAssembly_CalledTwiceForSameModule_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        var assembly = typeof(AssemblyMarker).Assembly;

        services.AddUseCasesFromAssembly(assembly, PipelineHarness.ModuleKey);
        var count = services.Count;
        services.AddUseCasesFromAssembly(assembly, PipelineHarness.ModuleKey);

        services.Count.ShouldBe(count);
        services.Count(d => d.ServiceType == typeof(IUseCaseBehavior<,>)).ShouldBe(3);
        services.Count(d => d.ServiceType == typeof(UseCaseModuleRegistry)).ShouldBe(1);
    }

    [Fact]
    public void AddUseCasesFromAssembly_SameAssemblyForSecondModule_Throws()
    {
        var services = new ServiceCollection();
        var assembly = typeof(AssemblyMarker).Assembly;
        services.AddUseCasesFromAssembly(assembly, PipelineHarness.ModuleKey);

        Should.Throw<InvalidOperationException>(() => services.AddUseCasesFromAssembly(assembly, "other"));
    }

    [Fact]
    public async Task GetModuleKey_RegisteredAndUnregisteredInputs_ReturnsKeyOrThrows()
    {
        await using var harness = new PipelineHarness();
        var registry = harness.Services.GetRequiredService<UseCaseModuleRegistry>();

        registry.GetModuleKey(typeof(CreateThingCommand)).ShouldBe(PipelineHarness.ModuleKey);
        Should.Throw<InvalidOperationException>(() => registry.GetModuleKey(typeof(string)));
    }
}
