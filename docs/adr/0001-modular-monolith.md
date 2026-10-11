# ADR-0001: Modular monolith

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

The template must let a team start a new service quickly, keep it maintainable as it grows, and leave the door open to splitting it later. Most new services do not yet know their final boundaries, cannot justify the operational cost of several deployables, and still need clear ownership of code and data. The template has no API gateway; it ships its own Auth module.

## Decision

Build one deployable (`src/Host/Acme.Service.Host/`) that hosts several modules under `src/Modules/`. Each module is a module project plus a `*.Contracts` project, owns one database schema, exposes only its `*Module` class publicly, and communicates with other modules only through Contracts interfaces and integration events. The boundaries are enforced by architecture tests (`tests/Acme.Service.ArchitectureTests/`).

## Options considered

### Option A: modular monolith with enforced boundaries (chosen)

- Pros: one build, one image, one deployment and one database to operate; in-process calls with no network failure modes; refactoring across modules is cheap while boundaries are still moving; modules can be extracted later because they already own their data and talk through contracts and events.
- Cons: modules share a process, so a memory leak or CPU spike in one affects all; scaling is all-or-nothing; boundary discipline depends on the architecture tests and reviews.

### Option B: microservices from the start

- Pros: independent deployment and scaling; hard isolation of failures and data.
- Cons: distributed transactions, network failure handling, service discovery, versioned APIs and per-service pipelines from day one; boundaries are expensive to move once wrong.

### Option C: classic layered monolith

- Pros: simplest structure, familiar.
- Cons: no data ownership; features reach into each other's tables; extraction later is a rewrite.

## Consequences

- Cross-module calls go through `*.Contracts` only; cross-module data uses snapshots or Contracts queries ([data ownership](../architecture/data-ownership.md)).
- No cross-module transactions: consistency between modules is eventual, through the outbox ([ADR-0004](0004-transactional-outbox-and-inbox.md)).
- A new module is a new pair of projects, a schema, its migrations, its permissions and one entry in `src/Host/Acme.Service.Host/Modules/HostModules.cs`.
- Rules R1 to R10 and the illegal-reference proof fail the build on a boundary violation.

## Related invariants

- INV-01: modules couple only through Contracts.
- INV-02: module internals are internal; the module class is the only public type.
- INV-06: each module owns exactly one schema.
- INV-07: no transaction spans two modules.
