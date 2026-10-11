# Infrastructure providers

> Applies to: Acme.Service template

One project per provider. Each implements one port from `src/BuildingBlocks/Acme.Service.BuildingBlocks/` and exposes an `IProviderRegistration` with a category and a name. The Host adds every registration with one line in `src/Host/Acme.Service.Host/Program.cs` (`builder.AddProviders(p => ...)`), and `<Category>:Provider` selects exactly one per category at startup ([ADR-0008](../../../docs/adr/0008-provider-model.md)). Only the Host references these projects (R10); modules and core BuildingBlocks never see their types or SDK namespaces (R3).

## Providers

| Project | Category and name | Port | Notes |
| --- | --- | --- | --- |
| `src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.InMemory/` | Cache `InMemory` | `ICache` | Per process. Allowed in Production with a warning when idempotency is on. |
| (same) | Blob `Local` | `IBlobStore` | Local disk under `Blob:Local:RootPath`; rejects path traversal and symlinks. Development only. |
| (same) | Notifications `Console` | `INotificationSender` | Logs recipient and subject, and the body in Development only; sends nothing. Development only. |
| (same) | Jobs `InMemory` | `IJobQueue` | In-process queue and Cronos schedules; 3 retries, lost on restart. Development only. |
| (same) | Locks `InMemory` | `IDistributedLock` | Per process. Development only. |
| `src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.Redis/` | Cache `Redis` | `ICache` | StackExchange.Redis; short timeouts, circuit breaker, `Degraded` readiness check; fails soft except `TryAddAsync`. |
| `src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.S3/` | Blob `S3` | `IBlobStore` | AWS S3 and S3-compatible stores (R2, MinIO, RustFS); presigned download URLs of at most 15 minutes. |
| `src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.AzureBlob/` | Blob `AzureBlob` | `IBlobStore` | Connection string (service SAS) or service URI with `DefaultAzureCredential` (user delegation SAS). |
| `src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.Smtp/` | Notifications `Smtp` | `INotificationSender` | MailKit; STARTTLS required for credentials; 30-second timeout. |
| `src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.Hangfire/` | Jobs `Hangfire` | `IJobQueue` | PostgreSQL storage in schema `hangfire`, a server in every replica, dashboard only in Development. LGPL-3.0. |
| `src/BuildingBlocks/Acme.Service.BuildingBlocks.Persistence/Locking/` | Locks `Postgres` | `IDistributedLock` | Not an Infra project: lives in Persistence because it needs only the database the service already has. |

"Development only" providers are refused at startup in the `Production` environment (D-21); the list is in `src/Host/Acme.Service.Host/Providers/ProviderRegistry.cs`. Every provider's keys, defaults and secrets are in [configuration](../../../docs/guides/configuration.md).

## Rules for provider projects

- Reference only core `BuildingBlocks` (Hangfire may also reference Persistence for its post-migration step). Package versions go in `Directory.Packages.props`.
- Public surface: the registration and its options class; everything else `internal`.
- Validate options at startup (`ValidateOnStart`) with messages that name the full key and never echo a secret.
- Never contact the backend during startup or registration; connect lazily with short timeouts.
- Report backend health as `Degraded`, never `Unhealthy`, unless the service truly cannot work without it (only PostgreSQL is `Unhealthy`).
- Keep provider-specific exceptions inside the project; the port stays neutral.
- Pass the shared contract suite in `tests/Acme.Service.Testing/Contracts/` (the subclasses are in `tests/Acme.Service.Infra.Tests/`).

## Known exceptions to "one line"

- Hangfire also needs `HangfireRegistration.AddSchemaMigration(...)` in `src/Host/Acme.Service.Host/Commands/MigrateCommand.cs` (the `migrate` host does not run the provider registry) and `app.MapHangfireDashboardIfEnabled()` in `Program.cs`: three Host touch points instead of one (INV-24 deviation, recorded in ADR-0008).
- A new development-only provider must be added to the list in `ProviderRegistry`.

To add a provider, or to point an existing one at a new service such as Cloudflare R2, follow [adding a provider](../../../docs/guides/adding-a-provider.md).
