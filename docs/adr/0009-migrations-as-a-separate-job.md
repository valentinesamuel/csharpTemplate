# ADR-0009: Migrations as a separate job

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Applying EF Core migrations at application startup is convenient but dangerous with several replicas: replicas race, a failed migration crash-loops every pod, and the application's database role needs DDL rights. The template also has to install the Hangfire schema and seed the permission catalog and roles.

## Decision

The web app never changes the schema (INV-16). The same binary has a `migrate` verb (`src/Host/Acme.Service.Host/Commands/MigrateCommand.cs`) that builds a minimal host with configuration, logging and each module's `AddPersistence`, then:

1. takes the PostgreSQL advisory lock `migrate` for the whole run;
2. applies each module's EF migrations in registration order, using `ConnectionStrings:PostgresMigrations`;
3. runs the post-migration steps in order: the Hangfire schema (order 100, only when `Jobs:Provider=Hangfire`), then the Auth permission and role seeder (order 200);
4. exits 0 on success and non-zero on any failure.

It is idempotent. `migrate --list` prints the pending migrations. Compose runs it as the one-shot `migrate` service (`deploy/compose/compose.yaml`); Kubernetes runs it as a Job (`deploy/k8s/migration/base/job.yaml`) applied and awaited before the app manifests.

## Options considered

### Option A: same image, `migrate` verb, run as a job (chosen)

- Pros: one artefact; the migration job can use a DDL role while the app uses a DML role; also installs the Hangfire schema and runs seeding; failures stop the deployment instead of crash-looping pods.
- Cons: deployment needs an ordered step (job, wait, then app).

### Option B: EF Core migration bundles

- Pros: a dedicated migration executable.
- Cons: a second artefact; does not cover Hangfire or seeding.

### Option C: DBA-applied SQL scripts

- Pros: full human review.
- Cons: slower; drift between scripts and the model. Still possible: `dotnet ef migrations script --idempotent` produces reviewable SQL.

## Consequences

- `/health/ready` reports 503 while any module has pending migrations, so a pod started before its migrations never receives traffic.
- Schema changes must be backward compatible with the previous app version (expand, deploy, contract), because old pods keep serving during a rollout.
- Concurrent `migrate` runs are serialised by the advisory lock.
- The migrations connection string and its secret are separate from the app's (`ConnectionStrings__PostgresMigrations`).

## Related invariants

- INV-16: no implicit migrations, Hangfire schema or seeding from the web app.
- INV-17: the migrate host validates its configuration and exits non-zero on failure.
- INV-06: each module's history table lives in its own schema.
