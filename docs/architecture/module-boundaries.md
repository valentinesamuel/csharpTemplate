# Module boundaries

> Applies to: Acme.Service template

A module is two projects under `src/Modules/<Name>/`: the module project, whose only public type is `<Name>Module`, and a `*.Contracts` project with the types other modules may use (permission constants, integration events, query interfaces and their DTOs). Everything else is `internal`.

## Reference matrix

| Project | May reference |
| --- | --- |
| `BuildingBlocks` | Nothing internal. NuGet: `Microsoft.Extensions.*.Abstractions` and FluentValidation only. |
| `BuildingBlocks.Web` | `BuildingBlocks`, plus the ASP.NET Core shared framework. |
| `BuildingBlocks.Persistence` | `BuildingBlocks`, EF Core, the Npgsql EF provider, naming conventions. |
| `BuildingBlocks.Infra.*` | `BuildingBlocks`. `Infra.Hangfire` may also reference `BuildingBlocks.Persistence` for its post-migration step. |
| `Modules.X.Contracts` | `BuildingBlocks` only. |
| `Modules.X` | `BuildingBlocks`, `BuildingBlocks.Web`, `BuildingBlocks.Persistence`, `Modules.X.Contracts`, and other modules' `*.Contracts`. |
| `Host` | Everything under `src/`. |

Never allowed: a module referencing any `Infra.*` project or another module's non-Contracts project.

## Rules and their enforcement

The rules run as tests in `tests/Acme.Service.ArchitectureTests/` (rule functions in `tests/Acme.Service.ArchitectureTests/ArchRules.cs`), so a violation fails `dotnet test` and CI.

| Rule | What it checks | Invariant |
| --- | --- | --- |
| R1 | A module never depends on another module's non-Contracts assembly (type dependencies and assembly references). | INV-01 |
| R2 | Contracts depend only on `BuildingBlocks` and the BCL. | INV-01 |
| R3 | Modules and core `BuildingBlocks` do not use `StackExchange.Redis`, `Amazon`, `Azure`, `MailKit`, `MimeKit`, `Hangfire`, `Npgsql` or any `Infra.*` namespace. EF-generated code in `<Assembly>.Persistence.Migrations` is the only exemption for `Npgsql`. | INV-03, INV-05 |
| R4 | In non-Auth modules, types that use EF Core live in a `*.Persistence*` namespace. Auth is exempt by assembly name. | INV-04, INV-05 |
| R5 | The only public type of a module assembly is its `*Module` class. | INV-02 |
| R6 | `BuildingBlocks*` never depends on `Acme.Service.Modules.*`. | INV-01 |
| R7 | Every integration event has a unique, well-formed `[IntegrationEvent("<module>.<name>.v<n>")]` name. | INV-19 |
| R8 | No use case is public. | INV-02 |
| R9 | No forbidden package (MediatR, AutoMapper, FluentAssertions, Moq, MassTransit v9+) in `Directory.Packages.props`, any csproj or lock file. | INV-25 |
| R10 | Only the Host references `Infra.*` projects; project references follow the matrix above. | INV-03 |
| Schema | Every module context maps all tables, sequences and its migrations history table to its own schema only. | INV-06 |
| Tokens | The template name appears only as one of the three init tokens (`tests/Acme.Service.ArchitectureTests/TemplateTokenTests.cs`). | INV-23 |

The rules are proven to bite: `tests/Acme.Service.ArchitectureTests.Fixtures.IllegalReference/` references the Auth module project directly, and `tests/Acme.Service.ArchitectureTests/IllegalReferenceProofTests.cs` asserts that R1 fails for it and names the offending type.

## How modules talk to each other

There are exactly two mechanisms, both through Contracts:

1. **Synchronous queries** through an interface declared in the provider's Contracts and implemented inside the provider module, for example `IUserDirectory` in `src/Modules/Auth/Acme.Service.Modules.Auth.Contracts/IUserDirectory.cs`. The call runs in-process and must not be made inside the caller's database transaction if it can block for long (INV-10).
2. **Integration events** published through the producer's outbox and consumed through the consumer's inbox ([messaging](messaging.md)). Events live in `Contracts/IntegrationEvents/<Name>V<n>.cs` and are additive-only within a version.

There is no HTTP between modules, no shared DbContext and no cross-module transaction (INV-07).

<!-- <sample:orders> -->

The Orders sample uses both: `src/Modules/Orders/Acme.Service.Modules.Orders/Jobs/SendOrderPlacedNotificationJob.cs` calls `IUserDirectory` to find the customer's address, and `src/Modules/Orders/Acme.Service.Modules.Orders/IntegrationEventHandlers/UserRegisteredHandler.cs` consumes Auth's `UserRegisteredV1` to keep a local customer snapshot.

<!-- </sample:orders> -->

## Inside a module

```text
src/Modules/<Name>/Acme.Service.Modules.<Name>/
  <Name>Module.cs            the single public type (IModule)
  Features/<UseCase>/        <UseCase>Command|Query.cs, <UseCase>UseCase.cs, <UseCase>Validator.cs, <UseCase>Endpoint.cs
  Domain/                    entities, value objects, repository ports
  Persistence/               <Name>DbContext.cs, Configurations/, Repositories/, Migrations/  (EF Core only here)
  IntegrationEventHandlers/  inbox consumers
  Jobs/                      IJob<TPayload> and IRecurringJob implementations
```

`IModule.Name` is the lower-case module name and equals the database schema and the key of the module's keyed `IUnitOfWork` and `IOutbox` ([ADR-0003](../adr/0003-schema-per-module.md)). Endpoints are discovered from every `IEndpoint` in the module assembly.

## Related decisions

- [ADR-0001 Modular monolith](../adr/0001-modular-monolith.md)
- [ADR-0005 Auth module uses Identity and EF Core directly](../adr/0005-auth-module-uses-identity-and-ef.md)
- [ADR-0016 EF Core confined to the Persistence namespace](../adr/0016-ef-confinement-to-persistence-namespace.md)
