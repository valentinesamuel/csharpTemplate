using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Acme.Service.BuildingBlocks.Tests.UseCases.Fixtures;

/// <summary>Container with this assembly's fixtures registered under module key "things" and a fake unit of work.</summary>
internal sealed class PipelineHarness : IAsyncDisposable
{
    public const string ModuleKey = "things";

    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;

    public PipelineHarness(Func<CancellationToken, Task<Result>>? commitStep = null)
    {
        UnitOfWork = Substitute.For<IUnitOfWork>();
        UnitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                Trace.Add("uow:begin");
                var operation = call.Arg<Func<CancellationToken, Task<Result>>>();
                var result = await operation(call.Arg<CancellationToken>());
                if (result.IsSuccess && commitStep is not null)
                {
                    result = await commitStep(call.Arg<CancellationToken>());
                }

                Trace.Add(result.IsSuccess ? "uow:commit" : "uow:rollback");
                return result;
            });

        Resolver = Substitute.For<IModuleUnitOfWorkResolver>();
        Resolver.Resolve(ModuleKey).Returns(UnitOfWork);

        var services = new ServiceCollection();
        services.AddSingleton(Trace);
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        services.AddSingleton(Resolver);
        services.AddUseCasesFromAssembly(typeof(AssemblyMarker).Assembly, ModuleKey);

        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _root.CreateAsyncScope();
    }

    public TraceLog Trace { get; } = new();

    public IUnitOfWork UnitOfWork { get; }

    public IModuleUnitOfWorkResolver Resolver { get; }

    public IServiceProvider Services => _scope.ServiceProvider;

    public IUseCaseExecutor<TIn, TOut> Executor<TIn, TOut>()
        where TIn : notnull => Services.GetRequiredService<IUseCaseExecutor<TIn, TOut>>();

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _root.DisposeAsync();
    }
}
