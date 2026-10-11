# ADR-0010: Hangfire with PostgreSQL storage

> Applies to: Acme.Service template

- **Status:** Accepted, licence confirmation open
- **Date:** 2026-10-08

## Context

Modules need background work: emails after registration, notifications after events, recurring maintenance such as outbox cleanup. Jobs must survive restarts in production, run on any replica, retry on failure and register recurring schedules once across replicas. PostgreSQL is already a hard dependency; adding a broker or Redis as a job store would add another one.

## Decision

Behind the `IJobQueue` port, the production job provider is Hangfire with PostgreSQL storage in the `hangfire` schema (`src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.Hangfire/`), selected with `Jobs:Provider=Hangfire`. `InMemory` is the development and test provider.

- Hangfire never creates its schema from the app (`PrepareSchemaIfNecessary=false`); `migrate` installs it through a post-migration step.
- The job runner is a closed generic type `HangfireJobRunner<TJob,TPayload>` that opens a DI scope, restores the correlation id and calls `IJob<TPayload>`. Payloads are System.Text.Json and should carry ids only.
- The dashboard is mapped at `/hangfire` only when `Jobs:Hangfire:DashboardEnabled=true` and the environment is Development, and only for local requests.

## Licence note

Hangfire.Core and Hangfire.AspNetCore are licensed under LGPL-3.0 (with a commercial option for Hangfire.Core), and Hangfire.PostgreSql is also LGPL-3.0, so the whole Hangfire stack used here is LGPL. The template consumes the packages unmodified as separate NuGet assemblies loaded at runtime, which is the usage the LGPL is designed to allow, and the rest of the template stays under its own licence. Teams must still:

- confirm with their organisation's open-source policy that LGPL-3.0 dependencies are acceptable (this confirmation is open);
- keep the licence notices of the LGPL packages with any distributed build;
- avoid modifying, merging or statically embedding the Hangfire assemblies (for example ILMerge-style merging) without legal review.

If LGPL is not acceptable, the `IJobQueue` port allows a replacement provider, for example a new `Infra` project based on Quartz.NET (Apache-2.0), with no module changes ([ADR-0008](0008-provider-model.md)).

## Options considered

### Option A: Hangfire with PostgreSQL storage (chosen)

- Pros: mature retries, scheduling and recurring jobs; one recurring enqueue per tick across replicas; uses the existing database; a dashboard for development.
- Cons: LGPL licence to confirm; scheduled jobs can run up to about 15 seconds late (polling); job types are persisted by CLR name, so renames orphan queued jobs.

### Option B: Quartz.NET

- Pros: Apache-2.0; clustering on PostgreSQL.
- Cons: more setup for persistence and clustering; no comparable dashboard. Kept as the fallback.

### Option C: in-memory only, or hosted services with timers

- Pros: no dependency.
- Cons: jobs are lost on restart; no cross-replica coordination.

## Consequences

- Hangfire needs three Host touch points instead of one line: the provider registration, the schema step in the migrate host, and the dashboard mapping.
- Without `migrate`, the app stays ready but Hangfire workers log errors and job calls fail until the schema exists.
- Drain the queues before renaming job or payload types, including before running `scripts/init.cs` on a service with persisted jobs.
- Long-running recurring jobs guard against overlap with `IDistributedLock`.
- Jobs are at-least-once; a job that sends email can, rarely, send it twice.

## Related invariants

- INV-16: the Hangfire schema is installed only by `migrate`.
- INV-24: the job provider is replaceable behind `IJobQueue`.
- INV-25: licences are recorded; LGPL is allowed only pending policy confirmation.
- INV-10: external effects run in jobs, outside database transactions.
