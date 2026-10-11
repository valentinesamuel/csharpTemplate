# BuildingBlocks

> Applies to: Acme.Service template

Shared, business-free code that every module builds on. Three projects plus one project per infrastructure provider. None of them references a module (architecture rule R6). The allowed references are in [module boundaries](../../docs/architecture/module-boundaries.md).

| Project | References | Role |
| --- | --- | --- |
| `src/BuildingBlocks/Acme.Service.BuildingBlocks/` | Nothing internal; only `Microsoft.Extensions.*.Abstractions` and FluentValidation | Ports and core types. Provider-neutral: no infrastructure package may appear here (R3). |
| `src/BuildingBlocks/Acme.Service.BuildingBlocks.Web/` | Core, ASP.NET Core shared framework | HTTP glue for modules. |
| `src/BuildingBlocks/Acme.Service.BuildingBlocks.Persistence/` | Core, EF Core, the Npgsql EF provider, naming conventions | EF Core base for module persistence, messaging, locks, migrations, readiness. |
| `src/BuildingBlocks/Infra/` | Core (Hangfire also Persistence, for its migration step) | One project per provider; see the [Infra README](Infra/README.md). |

## Core: `Acme.Service.BuildingBlocks`

| Folder | Types |
| --- | --- |
| `Results/` | `Result`, `Result<T>`, `Error(code, message, type)`, `ErrorType` (Validation, Unauthorized, Forbidden, NotFound, Conflict, Unprocessable, TooManyRequests, Unavailable). |
| `UseCases/` | `ICommand<TOut>`, `IQuery<TOut>`, `IRetryableCommand`, `IUseCase<TIn,TOut>`, `IUseCaseBehavior<TIn,TOut>`, `IUseCaseExecutor<TIn,TOut>`, the pipeline and `AddUseCasesFromAssembly`; `Behaviors/` holds Logging, Validation and Transaction, always in that order. |
| `Persistence/` | `IRepository<TEntity,TId>`, `Specification<T>` (criteria, sort, paging), `IUnitOfWork`, `IModuleUnitOfWorkResolver`. |
| `Messaging/` | `IIntegrationEvent`, `[IntegrationEvent("<module>.<name>.v<n>")]`, `IIntegrationEventHandler<T>`, `IOutbox`, `IEventBus`. |
| `Caching/` | `ICache` (get, set with TTL, remove, atomic `TryAddAsync`, `IsAvailable`). |
| `Blob/` | `IBlobStore` (put, open, delete, exists, optional download URL), `BlobKey`. |
| `Notifications/` | `INotificationSender`, `EmailMessage`. |
| `Jobs/` | `IJobQueue`, `IJob<TPayload>`, `IRecurringJob`. |
| `Locking/` | `IDistributedLock` (try-acquire with timeout, release on dispose). |
| `Paging/` | `PageRequest`, `PagedResult<T>`, `SortSpec`, `SortMap<T>` (sort whitelist). |
| `Security/` | `PermissionDefinition(name, description, defaultRoles)`. |
| `Providers/` | `IProviderRegistration`, `ProviderCategory` (Cache, Blob, Notifications, Jobs, Locks). |
| `Correlation/` | `ICorrelationContext`, so jobs and the outbox can carry the correlation id without a web dependency. |

## Web: `Acme.Service.BuildingBlocks.Web`

| Folder | Types |
| --- | --- |
| `Modules/` | `IModule`, `AddModules`, `MapModuleEndpoints`, `AddModuleRegistry` (for the command hosts), `ModuleRegistry`. |
| `Endpoints/` | `IEndpoint` and its discovery per module assembly. |
| `Results/` | `ToHttpResult()`: `Result` to HTTP status and RFC 7807 problem documents, with `Retry-After` on 503. |
| `Errors/` | ProblemDetails setup: `code`, `traceId` and `correlationId` on every problem, no details outside Development. |
| `Authorization/` | `RequirePermission("<resource>.<action>")` and the `perm:<name>` policy provider and handler. |
| `Idempotency/` | `AddIdempotency`, `.WithIdempotency()`: replay, in-progress and key-reuse handling on top of `ICache`. |
| `Paging/` | `BindListQuery`: `page`, `pageSize` and `sort` from the query string. |
| `Correlation/` | `X-Correlation-Id` middleware, accessor and outbound `HttpClient` handler. |

## Persistence: `Acme.Service.BuildingBlocks.Persistence`

| Area | Types |
| --- | --- |
| Module contexts | `ModuleDbContext` (schema, snake_case, outbox and inbox tables), `AddModuleDbContext<TContext>(configuration, moduleKey)` with `.AddRepository<TEntity,TId>()`, `EfRepository`, `EfUnitOfWork` (keyed by module), `DesignTime/ModuleDesignTimeFactoryBase`. |
| Messaging | `Outbox/` (`EfOutbox`, `OutboxDispatcher`, cleanup job and scheduler, `outbox.dead` metric), `Inbox/` (inbox decorator and store), `Messaging/` (`AddIntegrationEvents`, `AddIntegrationEventTypes`, `AddIntegrationEventHandler`, in-process bus, type registry, serializer). |
| Locks | `Locking/`: PostgreSQL advisory locks (`Locks:Provider=Postgres`) on a dedicated connection. |
| Migrations | `Migrations/`: `MigrationRunner` (advisory lock `migrate`, per-module migrations, post-migration steps), `IPostMigrationStep`, `MigrationTarget`. |
| Readiness | `Health/`: PostgreSQL connectivity and the pending-migrations check (cached 30 seconds). |
| Errors | `DatabaseErrors`: transient faults to 503 `database_unavailable`, unique violations and concurrency conflicts to 409. |

Only `src/BuildingBlocks/Acme.Service.BuildingBlocks.Persistence/` and module `Persistence` namespaces (plus the Auth module) may use EF Core types (INV-04).

## Changing BuildingBlocks

- Ports are shared by every module and provider. Prefer additive changes; a breaking port change touches every implementation and its contract suite in `tests/Acme.Service.Testing/Contracts/`.
- Never add a provider SDK to core or Web; it belongs in an `Infra.*` project ([adding a provider](../../docs/guides/adding-a-provider.md)).
- Never add business concepts here; a type used by two modules belongs in the owning module's Contracts.
- Tests live in `tests/Acme.Service.BuildingBlocks.Tests/` (pipeline, web glue, persistence and messaging with Testcontainers) and `tests/Acme.Service.Infra.Tests/`.

Related: [request lifecycle](../../docs/architecture/request-lifecycle.md), [messaging](../../docs/architecture/messaging.md), [error model](../../docs/architecture/error-model.md).
