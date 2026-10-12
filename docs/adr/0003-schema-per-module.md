# ADR-0003: One PostgreSQL schema per module

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Modules must own their data so they can evolve independently and be extracted later ([ADR-0001](0001-modular-monolith.md)). PostgreSQL is the only database in v1, but the persistence ports must stay neutral enough that a module could move to another store. Several module DbContexts live in one DI container and one process.

## Decision

Use one PostgreSQL database with one schema per module, named after the module (`IModule.Name`, lower case: `auth`, plus `hangfire` for the job store). Each module has its own `ModuleDbContext` subclass mapped only to its schema, its own migrations and its own `__ef_migrations_history` table inside the schema. The module's `IUnitOfWork` and `IOutbox` are keyed scoped services whose key is the module name; the transaction behaviour resolves the key from the use case's assembly.

## Options considered

### Option A: schema per module in one database (chosen)

- Pros: clear ownership visible in the database; one connection string and one backup; independent migrations per module; per-schema database roles possible; moving a module to its own database is a connection-string change.
- Cons: nothing in PostgreSQL itself stops a cross-schema join; discipline comes from tests and review.

### Option B: database per module

- Pros: hard isolation.
- Cons: more infrastructure and connection pools for a template; harder local setup; no benefit until modules are extracted.

### Option C: shared schema with table prefixes

- Pros: simplest.
- Cons: ownership is a naming convention only; migrations collide; extraction is painful.

### Keying alternatives

Keyed DI (`[FromKeyedServices("<module>")]`) was chosen over a generic `IUnitOfWork<TModuleMarker>`: it needs no marker types and the module key is already the schema name.

## Consequences

- No cross-schema foreign keys, joins, views or raw SQL; cross-module data uses Contracts calls or local snapshots ([data ownership](../architecture/data-ownership.md)).
- `MigrationRunner` rejects a context whose schema differs from its module key; the architecture tests check that every table and the history table of each context live in its schema.
- The outbox and inbox tables exist in every module schema.
- A unit of work spans exactly one module context; there are no cross-module transactions.

## Related invariants

- INV-06: schema ownership.
- INV-07: no cross-module transactions.
- INV-16: schemas change only through the migrate path (the `migrate` verb, or the opt-in `Migrations:RunOnStart` step that calls it) ([ADR-0009](0009-migrations-as-a-separate-job.md)).
