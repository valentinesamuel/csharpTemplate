# ADR-0009: Migrations as a separate job

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Applying EF Core migrations at application startup is convenient but dangerous with several replicas: replicas race, a failed migration crash-loops every pod, and the application's database role needs DDL rights. The template also has to install the Hangfire schema and seed the permission catalog and roles.

## Decision

The web host composition never changes the schema; schema changes happen only through the migrate path (INV-16). The same binary has a `migrate` verb (`src/Host/Acme.Service.Host/Commands/MigrateCommand.cs`) that builds a minimal host with configuration, logging and each module's `AddPersistence`, then:

1. takes the PostgreSQL advisory lock `migrate` for the whole run;
2. applies each module's EF migrations in registration order, using `ConnectionStrings:PostgresMigrations`;
3. runs the post-migration steps in order: the Hangfire schema (order 100, only when `Jobs:Provider=Hangfire`), then the Auth permission and role seeder (order 200);
4. exits 0 on success and non-zero on any failure.

It is idempotent. `migrate --list` prints the pending migrations. Compose runs it as the one-shot `migrate` service (`deploy/compose/compose.yaml`); a deployed environment either runs it as a one-shot step that is awaited before the app is started or updated, or opts in to running it at container start (Option D).

**Amendment (opt-in migrate at container start).** Setting `Migrations:RunOnStart=true` (env `Migrations__RunOnStart`, default `false`) makes the container run the `migrate` code path first, exactly as `dotnet Acme.Service.Host.dll migrate` does (advisory lock, module migrations, Hangfire schema, Auth seed), and start the web app only if it succeeded. The web host composition itself still never migrates; the step is the `migrate` verb's own code, called before the web host is built. It forwards the web app's environment and any `--Section:Key=value` arguments to the migrate step. On failure it logs "Migration failed; the web app was not started" and exits non-zero before any port opens. After a successful migration it unsets `ConnectionStrings__PostgresMigrations` in-process so the running web app's configuration never contains the DDL string.

On Render this is the way migrations run: `render.yaml` sets `Migrations__RunOnStart=true` and uses no pre-deploy command, so it works on the free plan. The service environment therefore holds `ConnectionStrings__PostgresMigrations` alongside the application string. The DDL/DML role separation is enforced at the PostgreSQL role level (the app connects as the DML role and never uses the migrator string); unsetting the variable in-process is defence in depth only. This is an accepted trade-off of the platform.

## Options considered

### Option A: same image, `migrate` verb, run as a job (chosen)

- Pros: one artefact; the migration job can use a DDL role while the app uses a DML role; also installs the Hangfire schema and runs seeding; failures stop the deployment instead of crash-looping pods.
- Cons: deployment needs an ordered step (job, wait, then app). Platforms without a one-shot job or pre-deploy hook on every plan cannot express it.

### Option D: opt-in migrate at container start (chosen for platforms like Render)

- Pros: no separate job or pre-deploy command, so it works on platforms where those are paid or unavailable (Render's free plan); reuses the `migrate` code path unchanged (lock, seed, Hangfire schema, idempotent); a failed migration exits 1 before any port opens, so the platform keeps the previous version; off by default, so Compose and other setups are unaffected.
- Cons: the service environment holds the DDL connection string (unset in-process after migrating, but present in the platform's environment); a long migration delays readiness because the port is not open yet; every replica runs the step (cheap when nothing is pending); the migrating container needs DDL rights at start, so the role split is only as strong as the Postgres roles.

### Option B: EF Core migration bundles

- Pros: a dedicated migration executable.
- Cons: a second artefact; does not cover Hangfire or seeding.

### Option C: DBA-applied SQL scripts

- Pros: full human review.
- Cons: slower; drift between scripts and the model. Still possible: `dotnet ef migrations script --idempotent` produces reviewable SQL.

## Consequences

- `/health/ready` reports 503 while any module has pending migrations, so a pod started before its migrations never receives traffic.
- Schema changes must be backward compatible with the previous app version (expand, deploy, contract), because old pods keep serving during a rollout.
- Concurrent `migrate` runs are serialised by the advisory lock, including replicas that start together with `Migrations:RunOnStart=true`.
- With `Migrations:RunOnStart=true`, a failed migration is exit 1 before any port opens: the platform treats the deploy as failed and keeps the previous version serving.
- With `Migrations:RunOnStart=true`, a long migration delays readiness because the port is not open yet. Run long migrations separately (the `migrate` verb as its own step), then deploy.
- With `Migrations:RunOnStart=true`, the DDL string is present in the service environment and is unset in-process once the migration succeeds; the real separation is the Postgres roles.
- The migrations connection string and its secret are separate from the app's (`ConnectionStrings__PostgresMigrations`).

## Related invariants

- INV-16: web host composition never migrates; schema changes happen only through the migrate path (the `migrate` verb, or the opt-in `Migrations:RunOnStart` step that calls it).
- INV-17: the migrate host validates its configuration and exits non-zero on failure.
- INV-06: each module's history table lives in its own schema.
