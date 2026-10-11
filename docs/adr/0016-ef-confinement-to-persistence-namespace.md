# ADR-0016: EF Core confined to the Persistence namespace

> Applies to: Acme.Service template

- **Status:** Accepted, pending design-review confirmation
- **Date:** 2026-10-08

## Context

PostgreSQL is the only store in v1, but the ports must stay neutral so a module could later move to another store (for example a document database) without rewriting its use cases. Each module is deliberately only two projects (module plus Contracts). Without a rule, EF Core types (`DbContext`, `DbSet`, `IQueryable` extensions) leak into use cases and endpoints and the "replace only the persistence layer" claim stops being true.

## Decision

In every module except Auth, types that depend on `Microsoft.EntityFrameworkCore` live only in `*.Persistence*` namespaces (the module's `Persistence/` folder: DbContext, configurations, repositories, migrations). Use cases depend only on `IRepository<TEntity,TId>`, `Specification<T>`, `IUnitOfWork` and module-specific repository ports declared in `Domain/`. The rule is R4 in `tests/Acme.Service.ArchitectureTests/PersistenceConfinementTests.cs`; Auth is exempt by assembly name ([ADR-0005](0005-auth-module-uses-identity-and-ef.md)).

## Options considered

### Option A: namespace confinement enforced by an architecture test (chosen)

- Pros: keeps two projects per module; the boundary is checked on every test run; a different store replaces the `Persistence/` folder and its registration only.
- Cons: the boundary is inside one assembly, so `internal` EF types are technically visible to use cases and only the test prevents their use.

### Option B: a third `Infrastructure` project per module

- Pros: the compiler enforces the boundary.
- Cons: triples the project count across modules; more references and ceremony for every new module.

### Option C: EF Core allowed anywhere

- Pros: least ceremony.
- Cons: EF leaks into use cases; changing the store becomes a rewrite.

## Consequences

- Queries that need EF features (no-tracking reads, includes, projections) are added to a module repository port implemented in `Persistence/Repositories/`.
- Use cases inject the module's keyed `IUnitOfWork` and repository ports, never the DbContext.
- EF-generated migrations reference Npgsql metadata; the `<Assembly>.Persistence.Migrations` namespace is exempt from the Npgsql ban (R3) for that reason only.
- `dotnet ef migrations add` generates public classes; they must be made `internal` to satisfy R5.

<!-- <sample:orders> -->

The Orders sample is the reference implementation: `src/Modules/Orders/Acme.Service.Modules.Orders/Domain/IOrderRepository.cs` is the port, `src/Modules/Orders/Acme.Service.Modules.Orders/Persistence/Repositories/OrderRepository.cs` the EF implementation, and the use cases under `src/Modules/Orders/Acme.Service.Modules.Orders/Features/` contain no EF types.

<!-- </sample:orders> -->

## Related invariants

- INV-04: persistence confinement for non-Auth modules.
- INV-05: the Auth exception.
- INV-03: no `Npgsql` or other provider types in modules outside generated migrations.
