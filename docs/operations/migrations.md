# Database migrations

> Applies to: Acme.Service template

The web host composition never migrates; schema changes happen only through the migrate path: the `migrate` verb of the service image, or the opt-in `Migrations:RunOnStart` step that calls it before the web app starts (INV-16, [ADR-0009](../adr/0009-migrations-as-a-separate-job.md)). Each module owns one schema with its own EF Core migrations and its own history table (`<schema>.__ef_migrations_history`).

## What `migrate` does

`src/Host/Acme.Service.Host/Commands/MigrateCommand.cs` builds a minimal host (configuration, logging, Data Protection and `IModule.AddPersistence` for every module; no JWT, SMTP or blob settings needed) and then:

1. takes the PostgreSQL advisory lock `migrate` for the whole run, so concurrent runs are serialised;
2. applies each module's pending migrations in the order of `src/Host/Acme.Service.Host/Modules/HostModules.cs` (Auth first), over `ConnectionStrings:PostgresMigrations`;
3. runs the post-migration steps in order: the Hangfire schema install or upgrade (only when `Jobs:Provider=Hangfire` is set for the run), then the Auth seeder, which re-syncs the permission catalog and the seed roles;
4. exits 0, or 1 on any failure (2 on invalid arguments).

It is idempotent: a second run applies nothing and re-runs the seeder harmlessly.

```bash
# with task (https://taskfile.dev; docs/guides/task-commands.md): list, then apply
task db:migrate:list
task db:migrate

# list pending migrations per module context, change nothing
dotnet run --project src/Host/Acme.Service.Host -- migrate --list --environment=Development
# auth (AuthDbContext): 0 pending
#   ...one line per module context, each pending migration indented below it

# apply
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate
```

<!-- <sample:orders> -->

With the Orders sample and a database migrated before its second migration existed, `--list` prints:

```text
auth (AuthDbContext): 0 pending
orders (OrdersDbContext): 1 pending
  20261009144303_AddSnapshotsAndAttachments
```

<!-- </sample:orders> -->

The verbs read `DOTNET_ENVIRONMENT` (or `--environment=...`), not the launch profile's `ASPNETCORE_ENVIRONMENT`; without one of them a local run is `Production` and finds no connection string. Other configuration can be passed as `--Section:Key=value`, for example `--ConnectionStrings:PostgresMigrations=<connection string>` (mind your shell history).

| Where | With task | How |
| --- | --- | --- |
| Local | `task db:migrate` (`task db:migrate:list` to list) | `DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate` |
| Docker Compose | `task docker:migrate` | The one-shot `migrate` service runs on every `up`; re-run with `docker compose -f deploy/compose/compose.yaml run --rm migrate` |
| Deployed | Render: none (it deploys on push); another Docker host: `task deploy:migrate`, or `task deploy:release` ([deploy with task](deployment.md#deploy-with-task)) | On Render, `Migrations__RunOnStart=true` (set in `render.yaml`): the container runs the same migrate step first and starts the web app only if it succeeded; a failure exits 1 before any port opens, so the previous version keeps serving; on another Docker host, a one-shot container of the same image, run and awaited before the app starts ([deployment](deployment.md#deploy-order)) |

`Migrations:RunOnStart` (env `Migrations__RunOnStart`, default `false`) runs exactly the `migrate` code path (advisory lock, module migrations, Hangfire schema, Auth seed; idempotent) before the web app. It forwards the web app's environment (`ASPNETCORE_ENVIRONMENT`) and any `--Section:Key=value` arguments to the migrate step, requires `ConnectionStrings__PostgresMigrations`, and unsets that variable in-process afterwards so the running app's configuration does not contain the DDL string (defence in depth; the real separation is the Postgres roles). Replicas that start together are serialised by the advisory lock. On failure it logs "Migration failed; the web app was not started" and exits non-zero. Keep it `false` locally: Compose uses the one-shot `migrate` service.

Until every module is migrated, `/health/ready` returns 503 and new instances do not receive traffic; the result of the pending-migrations check is cached for 30 seconds.

## Creating a migration

Run from the repository root; the Host is the startup project and each module's design-time factory supplies the context. With [task](../guides/task-commands.md), `MODULE` fills in the options:

```bash
task db:add MODULE=<Name> NAME=<MigrationName>
```

Without task:

```bash
dotnet ef migrations add <MigrationName> \
  --project src/Modules/<Name>/Acme.Service.Modules.<Name> \
  --startup-project src/Host/Acme.Service.Host \
  --context <Name>DbContext \
  --output-dir Persistence/Migrations
```

For Auth: `task db:add MODULE=Auth NAME=<MigrationName>`, or `--project src/Modules/Auth/Acme.Service.Modules.Auth --context AuthDbContext`.

<!-- <sample:orders> -->

For the Orders sample:

```bash
dotnet ef migrations add AddOrderNotes \
  --project src/Modules/Orders/Acme.Service.Modules.Orders \
  --startup-project src/Host/Acme.Service.Host \
  --context OrdersDbContext \
  --output-dir Persistence/Migrations
```

<!-- </sample:orders> -->

Then:

1. **Make the generated classes `internal`.** `dotnet ef` emits public classes in the migration, its `.Designer.cs` and the model snapshot (it also drops the `internal` modifier of the existing snapshot class); the architecture test R5 fails on them.
2. **Review the SQL** before committing (below), especially for locks on large tables.
3. **Check compatibility** with the previous release (expand/contract, below).

`migrations add` does not connect to a database. Commands that do (`list` with status, `script` for a specific database) read the connection string from the `ConnectionStrings__PostgresMigrations` environment variable, falling back to `Host=localhost;Port=5432;Database=acme_service;Username=postgres`.

Other commands, with the same `--project`, `--startup-project` and `--context` options:

| Purpose | With task | Command |
| --- | --- | --- |
| List migrations | `task db:list MODULE=<Name>` | `dotnet ef migrations list ... --no-connect` |
| Remove the last, unapplied migration | `task db:remove MODULE=<Name>` | `dotnet ef migrations remove ...` (connects to the database to check that it is unapplied; check the snapshot's `internal` modifier afterwards) |
| Idempotent SQL for review or DBA-applied deployments | `task db:script MODULE=<Name>` (default output `artifacts/sql/<Name>.sql`, gitignored) | `dotnet ef migrations script --idempotent ... --output <file>.sql` |

If two branches both add a migration to the same module, regenerate one of them after merging instead of hand-merging the model snapshot.

## Expand/contract

The migration runs before the new instances start, and the old instances may keep serving during the replacement, so the schema after a migration must work for **both** the old and the new code.

| Change | Release N (expand) | Release N+1 or later (contract) |
| --- | --- | --- |
| Add a required column | Add it nullable or with a default; new code writes it; backfill existing rows. | Make it `NOT NULL`. |
| Rename a column | Add the new column; write both; read the new one falling back to the old; backfill. | Stop writing the old column, then drop it in a later release. |
| Drop a column or table | Stop reading and writing it (code only). | Drop it. |
| Change a type | Add a column of the new type alongside; dual-write; backfill. | Switch reads, then drop the old column. |
| Add an index on a large table | Add it in its own migration; consider `CREATE INDEX CONCURRENTLY` through `migrationBuilder.Sql` with `suppressTransaction: true`, and mind the time limit below. | |
| Rename or remove a permission | Declare the **new** permission with the same default roles and keep the old one declared; move the endpoints to the new name. | Remove the old declaration. |

Permissions follow the same rule because the seeder is part of `migrate`: it deletes every permission no module declares, together with its role grants, as soon as the migration of the new release runs, which is **before** the old instances are gone. Tokens issued from then on no longer carry the old name, so old instances that still check it answer 403 for the rest of the rollout. Declaring the old name again later re-applies only the seed roles' declared defaults; any grant that was not a default (for example one added by hand in SQL) is gone.

Destructive changes belong only in contract releases. Rollback is "redeploy the previous image", never a down migration, which is why each release must leave a schema the previous release can use.

Integration events are additive-only too ([integration events](../guides/integration-events.md#versioning)); the same discipline applies to Contracts DTOs used across modules.

## Time limit

The deployment procedure allows the one-shot migration container (or, with `Migrations:RunOnStart=true`, the container's start-up migration) 15 minutes: all migrations of all modules, the Hangfire schema step and the seeder should finish within that, or the deployment stops (the limit is your pipeline's own timeout, not enforced by the service). Long backfills and index builds on large tables do not belong in a migration: add the schema change in the migration, and run the backfill as an idempotent background job ([background jobs](../guides/background-jobs.md)) or a separately planned maintenance step, then contract in a later release. If a migration genuinely needs longer, raise the timeout of the deployment procedure ([deployment](deployment.md#deploy-order)). With `Migrations:RunOnStart=true` the port is not open until the migration finishes, so a long migration delays readiness (and may hit the platform's health-check timeout); run such migrations separately with the `migrate` verb before deploying.

## Hangfire schema

The Hangfire schema is not an EF migration. `migrate` installs or upgrades it with the installer shipped in Hangfire.PostgreSql when the run has `Jobs:Provider=Hangfire`. After updating the Hangfire packages, run `migrate` before the new app instances start, as for any migration. `Jobs:Hangfire:Schema` must be the same for the migration and the app.

## Seeding

The Auth seeder is a post-migration step, not a migration: it reads the permissions declared in code by every module and makes the database match ([permissions](../guides/permissions.md)). It runs on every `migrate`, in one transaction under its own advisory lock.

## Failure handling

| Situation | Result |
| --- | --- |
| A migration fails | `migrate` exits 1; already-applied migrations of earlier modules stay; the app keeps running on the old instances. Fix and re-run. |
| Two runs at once | The second waits for the advisory lock, then finds nothing to do. |
| The app started before `migrate` | It does not create anything; readiness is 503; the outbox dispatchers and Hangfire workers log warnings until the schema exists. |
| Wrong connection string or missing DDL rights | `migrate` fails with the PostgreSQL error; the connection string is never logged. |
