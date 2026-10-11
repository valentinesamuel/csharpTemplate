# ADR-0002: Hand-rolled use-case pipeline, no MediatR

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Every use case needs the same cross-cutting steps: structured logging without leaking inputs, validation, and a transaction for commands that also flushes the outbox. These steps must apply to every caller, not only HTTP: background jobs and integration event handlers invoke use cases too. MediatR, the usual choice, moved to a commercial licence, which the template excludes (INV-25).

## Decision

Implement a small open-generic pipeline in core BuildingBlocks: `IUseCase<TIn,TOut>` returns `Result<TOut>`; `IUseCaseExecutor<TIn,TOut>` is implemented by `UseCasePipeline<TIn,TOut>` (`src/BuildingBlocks/Acme.Service.BuildingBlocks/UseCases/UseCasePipeline.cs`), which wraps the use case in the registered `IUseCaseBehavior<TIn,TOut>` instances. `AddUseCasesFromAssembly(assembly, moduleKey)` registers use cases, FluentValidation validators and the behaviours in the fixed order Logging, Validation, Transaction. Inputs mark themselves `ICommand<TOut>` (transactional) or `IQuery<TOut>` (read-only). Validation uses FluentValidation in the pipeline; the built-in minimal-API validation is not enabled, to avoid validating twice.

## Options considered

### Option A: own open-generic pipeline (chosen)

- Pros: no extra dependency or licence; about a hundred lines that the team owns; works identically for HTTP, jobs and consumers; typed end to end, no reflection at call time; behaviour order is explicit and tested.
- Cons: no ecosystem of ready-made behaviours; notifications and streaming are not provided (integration events cover the notification need).

### Option B: decorators via Scrutor

- Pros: standard decorator pattern, no custom executor.
- Cons: another dependency; decorator order is registration-order magic spread across modules; harder to make the transaction behaviour depend on the input kind.

### Option C: endpoint filters

- Pros: built into ASP.NET Core.
- Cons: only runs for HTTP; jobs and consumers would bypass validation and transactions.

### Option D: MediatR

- Pros: well known, many examples.
- Cons: commercial licence for current versions (INV-25); more indirection than the template needs.

## Consequences

- Endpoints, jobs and handlers call `IUseCaseExecutor<TIn,TOut>`, never the use case directly.
- `TransactionBehavior` maps the input's assembly to the module key and resolves that module's keyed `IUnitOfWork`, so a use case cannot commit through another module's context.
- Exceptions propagate unchanged to the global handler; expected failures are `Result` values.
- Inputs that must commit writes even on failure (login lockout counters, refresh reuse revocation) or must keep I/O outside the transaction are deliberately not `ICommand`; see [request lifecycle](../architecture/request-lifecycle.md).
- Tests: `tests/Acme.Service.BuildingBlocks.Tests/UseCases/UseCasePipelineTests.cs` and `tests/Acme.Service.BuildingBlocks.Tests/UseCases/TransactionBehaviorTests.cs`.

## Related invariants

- INV-25: no MediatR or other commercially licensed package.
- INV-26: only queries and `IRetryableCommand` inputs are retried on transient faults.
- INV-08: the transaction behaviour persists outbox rows with the business change.
- INV-28: the logging behaviour never logs inputs.
