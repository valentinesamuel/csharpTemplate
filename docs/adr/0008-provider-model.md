# ADR-0008: Provider model for infrastructure

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

The template must run on a laptop with no cloud services, in tests with containers, and in production on different clouds. Cache, blob storage, email, jobs and locks each have several implementations (Redis or in-memory; S3-compatible, Azure Blob or local disk; SMTP or console; Hangfire or in-memory; PostgreSQL advisory locks or in-memory). Modules must not know which one is in use, and adding a provider must not touch modules or core BuildingBlocks.

## Decision

Modules depend only on ports in core BuildingBlocks (`ICache`, `IBlobStore`, `INotificationSender`, `IJobQueue`, `IDistributedLock`). Each implementation lives in its own project under `src/BuildingBlocks/Infra/` and exposes an `IProviderRegistration` with a `Category`, a `Name` and `Register(services, configuration)`. The Host lists the available registrations with one line each in `builder.AddProviders(...)` in `src/Host/Acme.Service.Host/Program.cs`; `src/Host/Acme.Service.Host/Providers/ProviderRegistry.cs` reads `<Category>:Provider` and registers exactly one per category.

Startup fails when a category's value is missing, unknown or ambiguous, and when a development-only provider (`Local` blob, `Console` notifications, `InMemory` jobs and locks) is selected in `Production`. `Cache:Provider=InMemory` is allowed in Production; a startup warning is logged only when idempotency is enabled.

## Options considered

### Option A: registration objects selected by configuration (chosen)

- Pros: adding a provider is one project, one line and one configuration value; typos fail fast with the key named; every provider can be tested against a shared contract suite (`tests/Acme.Service.Testing/Contracts/`).
- Cons: all provider assemblies ship in the image even when unused.

### Option B: an if/else chain in the Host

- Pros: obvious.
- Cons: grows with every provider; easy to forget validation; harder to test.

### Option C: compile-time flags

- Pros: smaller images.
- Cons: one build per configuration; provider swaps need a rebuild.

## Consequences

- Swapping, for example, local disk for S3 is configuration only; an S3-compatible service such as Cloudflare R2 is also configuration only.
- A new cache provider is a new `Infra.*` project implementing `ICache` plus its registration, one `p.Add<...>()` line and one configuration value, plus a subclass of the contract test suite.
- Provider-specific exceptions stay in their Infra project; ports stay neutral.
- Exceptions to "one line": Hangfire also needs a schema step registered in the `migrate` command host and a dashboard mapping, because `migrate` does not run the provider registry ([ADR-0010](0010-hangfire-with-postgres-storage.md)).
- The architecture tests forbid modules and core BuildingBlocks from referencing Infra projects or provider SDK namespaces.

## Related invariants

- INV-03: no concrete provider types in modules or core BuildingBlocks.
- INV-17: unknown, missing or development-only providers fail startup.
- INV-24: provider extensibility without module changes.
