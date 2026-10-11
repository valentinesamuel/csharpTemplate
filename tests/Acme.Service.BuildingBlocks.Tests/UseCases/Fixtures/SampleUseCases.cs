using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using FluentValidation;

namespace Acme.Service.BuildingBlocks.Tests.UseCases.Fixtures;

// Discovered by AddUseCasesFromAssembly when the tests scan this assembly.

public sealed record CreateThingCommand(string Name) : ICommand<Guid>;

public sealed record RenameThingCommand(string Name) : ICommand<string>, IRetryableCommand;

public sealed record GetThingQuery(string Name) : IQuery<string>;

public sealed record ExplodeCommand : ICommand<int>;

internal sealed class CreateThingUseCase(TraceLog trace) : IUseCase<CreateThingCommand, Guid>
{
    public static readonly Guid CreatedId = new("0190a5c4-0000-7000-8000-000000000001");

    public Task<Result<Guid>> ExecuteAsync(CreateThingCommand input, CancellationToken cancellationToken)
    {
        trace.Add("use-case");
        return Task.FromResult<Result<Guid>>(
            input.Name == "taken" ? Error.Conflict("thing_exists", "Thing exists.") : CreatedId);
    }
}

internal sealed class CreateThingValidator : AbstractValidator<CreateThingCommand>
{
    public CreateThingValidator(TraceLog trace)
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Name)
            .MustAsync(async (name, ct) =>
            {
                await Task.Yield();
                trace.Add("validator");
                return name != "forbidden";
            })
            .WithMessage("Name is not allowed.");
    }
}

internal sealed class RenameThingUseCase(TraceLog trace) : IUseCase<RenameThingCommand, string>
{
    public Task<Result<string>> ExecuteAsync(RenameThingCommand input, CancellationToken cancellationToken)
    {
        trace.Add("use-case");
        return Task.FromResult<Result<string>>(input.Name);
    }
}

internal sealed class GetThingUseCase(TraceLog trace) : IUseCase<GetThingQuery, string>
{
    public Task<Result<string>> ExecuteAsync(GetThingQuery input, CancellationToken cancellationToken)
    {
        trace.Add("use-case");
        return Task.FromResult<Result<string>>(input.Name);
    }
}

internal sealed class ExplodeUseCase : IUseCase<ExplodeCommand, int>
{
    public Task<Result<int>> ExecuteAsync(ExplodeCommand input, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("boom");
}
