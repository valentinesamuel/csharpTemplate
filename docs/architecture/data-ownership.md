# Data ownership

> Applies to: Acme.Service template

There is one PostgreSQL database (`acme_service` in local development) and one schema per module. A module's DbContext maps only to its own schema, and no module reads or writes another module's tables ([ADR-0003](../adr/0003-schema-per-module.md), INV-06).

## Schemas

| Schema | Owner | Contents |
| --- | --- | --- |
| `auth` | Auth module (`src/Modules/Auth/Acme.Service.Modules.Auth/Persistence/AuthDbContext.cs`) | Identity tables (`users`, `roles`, `user_roles`, `user_claims`, `user_logins`, `user_tokens`, `role_claims`), `permissions`, `role_permissions`, `sessions`, `refresh_tokens`, `mfa_challenges`, `totp_time_steps`, `external_login_codes`, `data_protection_keys`, plus the shared messaging tables. |
| `hangfire` | `src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.Hangfire/` | Hangfire job storage, installed only by `migrate` when `Jobs:Provider=Hangfire`. |

<!-- <sample:orders> -->

| Schema | Owner | Contents |
| --- | --- | --- |
| `orders` | Orders sample (`src/Modules/Orders/Acme.Service.Modules.Orders/Persistence/OrdersDbContext.cs`) | `orders`, `order_lines`, `order_attachments`, `customer_snapshots`, plus the shared messaging tables. |

<!-- </sample:orders> -->

Every module schema also contains:

- `outbox_messages` and `inbox_messages` ([messaging](messaging.md)), mapped by `ModuleDbContext` in `src/BuildingBlocks/Acme.Service.BuildingBlocks.Persistence/ModuleDbContext.cs`;
- `__ef_migrations_history`, so each module's migrations are versioned independently.

## Conventions

- Schema name = `IModule.Name` = the keyed unit-of-work key. `MigrationRunner` refuses a context whose schema differs from its module key.
- snake_case identifiers (EFCore.NamingConventions), plural table names.
- Primary keys are `id uuid` generated as Guid v7.
- Timestamps are `timestamptz` in UTC, named `*_at`, and come from the injected `TimeProvider`.
- Optimistic concurrency uses the PostgreSQL `xmin` system column where a resource is updated concurrently.

## No cross-schema access

Forbidden: foreign keys, joins, views and raw SQL that touch another module's schema, and transactions that span two module contexts (INV-07). `tests/Acme.Service.ArchitectureTests/` checks that every context's model lives in a single schema; the rest is enforced by review and by the keyed unit of work, which can only commit the context of the module that owns the use case.

Why: a module that only ever touches its own schema can move to its own database, or to a different store behind the same `IRepository` and `IUnitOfWork` ports, without changes elsewhere.

## Getting another module's data

When a module needs data owned by another module, pick one of two patterns.

| Pattern | Use it when | Trade-off |
| --- | --- | --- |
| **Contracts call.** Call an interface from the owner's Contracts, implemented by the owner (for example `IUserDirectory` in `src/Modules/Auth/Acme.Service.Modules.Auth.Contracts/IUserDirectory.cs`). | You need the current value at the moment of use, for one or a few records, and it is acceptable to depend on the owner being available. | Synchronous coupling; not usable for lists, filters or sorting across modules. Never call it inside your own transaction if it can block. |
| **Local snapshot.** Consume the owner's integration events through your inbox and upsert a read model in your own schema. | You need to filter, sort, join or list by the other module's data, or you must keep working when the owner is unavailable. | Eventually consistent; the owner must publish events with the fields you need; you own the snapshot's freshness logic. |

Snapshots must be idempotent and tolerate out-of-order delivery: compare the event's `OccurredAt` with the stored `updated_at` and ignore older events.

<!-- <sample:orders> -->

The Orders sample uses both patterns:

- `orders.customer_snapshots` is fed by `src/Modules/Orders/Acme.Service.Modules.Orders/IntegrationEventHandlers/UserRegisteredHandler.cs` from Auth's `UserRegisteredV1`, so order lists can show customer data without joining `auth.users`.
- `src/Modules/Orders/Acme.Service.Modules.Orders/Jobs/SendOrderPlacedNotificationJob.cs` calls `IUserDirectory.GetUserSummaryAsync` at send time, outside any transaction, to get the current email address; a deleted user means the email is skipped.

<!-- </sample:orders> -->

## Database roles

`ConnectionStrings:Postgres` is used by the web app for data access (DML) and `ConnectionStrings:PostgresMigrations` by the `migrate` verb for schema changes (DDL). In development both point to the same superuser; in a real deployment give them different roles. The app role needs `SELECT` on each `<schema>.__ef_migrations_history` for the readiness check.
