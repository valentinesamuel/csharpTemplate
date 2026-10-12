# Failure modes

> Applies to: Acme.Service template

This is the catalogue of failure modes the template was designed against, and what the code actually does in each case. "Evidence" names the test that pins the behaviour where one exists; rows without evidence are design rules enforced by review or documented operational limits.

## Startup and configuration

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| A required configuration value is missing or invalid | Options are validated on start (INV-17). The process logs `Invalid configuration:` with every failing key (for example `Observability:ServiceName is required.`) and exits with code 1 before serving traffic. | |
| `<Category>:Provider` missing, unknown or ambiguous | Startup fails naming the key; all provider errors are reported together and nothing is registered. Names match case-insensitively. | `tests/Acme.Service.IntegrationTests/ProviderSelectionTests.cs`, `tests/Acme.Service.Infra.Tests/Providers/ProviderRegistryTests.cs` |
| A development-only provider (`Blob:Provider=Local`, `Notifications:Provider=Console`, `Jobs:Provider=InMemory`, `Locks:Provider=InMemory`) in `Production` | Startup fails. `Cache:Provider=InMemory` is allowed but logs a warning because idempotency then works per replica only. | `tests/Acme.Service.IntegrationTests/ProviderSelectionTests.cs` |
| JWT key ring misconfigured (no or two `Active` keys, duplicate `Kid`, RSA below 2048 bits, both or neither key source) | Startup fails; it never falls back to an insecure mode. | `tests/Acme.Service.Modules.Auth.Tests/Tokens/KeyRingStartupTests.cs` |
| External OIDC enabled with incomplete settings | Startup fails (Authority, ClientId, ClientSecret, non-empty `AllowedReturnUrls`, `openid` scope; HTTPS authority outside Development). | `tests/Acme.Service.Modules.Auth.Tests/Unit/ExternalLoginUnitTests.cs` |
| A required secret or setting is missing from the Render environment | The instance fails at start through options validation, naming the key, and never becomes ready; on Render the previous deploy keeps serving. | |
| `Http:ForwardedHeaders` has no known networks or proxies (and `TrustAllProxies` is off) outside Development | A startup warning; only loopback proxies are trusted, so behind a proxy every client appears as the proxy's address. | |

## HTTP pipeline

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Middleware misordered (forwarded headers not first) | Prevented by construction: `src/Host/Acme.Service.Host/Program.cs` applies forwarded headers first, then correlation, request logging, the exception handler, routing, rate limiting, authentication, authorization. See [request lifecycle](request-lifecycle.md). | |
| Malicious `X-Correlation-Id` (too long, CRLF, several values) | Rejected and replaced by a generated id; the inbound value is never echoed or logged. | `tests/Acme.Service.IntegrationTests/HostSmokeTests.cs` |
| Secrets in request logs | The request log records method, path without query string, status and duration; no headers. Use-case logs never include inputs. | `tests/Acme.Service.Modules.Auth.Tests/Password/SensitiveDataLoggingTests.cs` |
| An exception escapes a use case | The pipeline does not catch it; the exception handler returns 500 `internal_error` without message or stack trace outside Development. | `tests/Acme.Service.BuildingBlocks.Tests/UseCases/UseCasePipelineTests.cs` |
| Behaviour order changes because of DI registration order | Fixed in one place (`AddUseCasesFromAssembly`): Logging, Validation, Transaction; idempotent when called per module. | `tests/Acme.Service.BuildingBlocks.Tests/UseCases/UseCasePipelineTests.cs` |
| Invalid input | Validation runs asynchronously before the use case and returns 400 `validation_failed`; the use case is never invoked. | `tests/Acme.Service.BuildingBlocks.Tests/UseCases/UseCasePipelineTests.cs` |
| Unknown `sort` field or bad paging | 400 `invalid_sort` or `invalid_paging`; `pageSize` above 100 is clamped. | `tests/Acme.Service.BuildingBlocks.Tests/Web/ListQueryBindingTests.cs` |
| An endpoint forgot its authorization | The fallback policy requires an authenticated user; an anonymous endpoint that is not in the allowlist fails the allowlist test. Unmatched routes also return 401 without a token. | `tests/Acme.Service.IntegrationTests/Security/AnonymousEndpointAllowlistTests.cs` |
| A non-`perm:` policy name | The permission policy provider falls back to the default provider, so named policies such as `live-session` keep working. | `tests/Acme.Service.BuildingBlocks.Tests/Web/PermissionAuthorizationTests.cs` |

## Idempotency

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Two identical first requests race | The atomic `ICache.TryAddAsync` marker lets one run; the other gets 409 `idempotency_in_progress`. | `tests/Acme.Service.BuildingBlocks.Tests/Web/IdempotencyFilterTests.cs` |
| Same key, different body | 422 `idempotency_key_reused`. | `tests/Acme.Service.BuildingBlocks.Tests/Web/IdempotencyFilterTests.cs` |
| Replay of a stored response | Status, body, `Content-Type` and `Location` only, with `Idempotent-Replayed: true`; never `Set-Cookie`. 5xx, 409 and 429 responses are not stored. | `tests/Acme.Service.BuildingBlocks.Tests/Web/IdempotencyFilterTests.cs` |
| Cache unavailable | `Idempotency:FailMode=Closed` (default): 503 `idempotency_unavailable`. `Open`: the request proceeds without idempotency and a warning is logged. Requests without the header are unaffected. | `RedisDownTests` (integration tests) |
| Stored response larger than the cache value limit | Treated as a store failure: the marker is removed and the response is returned but not stored. | |

## Database

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| PostgreSQL blip or failover during a query | Reads outside a transaction run under a retrying strategy (3 retries, at most 2 seconds apart). If retries are exhausted the exception propagates and the request returns 500 `internal_error` (not 503; see the note below). | `PostgresBlipTests` (integration tests) |
| PostgreSQL blip during a command | Not retried unless the input implements `IRetryableCommand`; the transaction rolls back and the client gets 503 `database_unavailable` with `Retry-After: 5`. Retrying with the same `Idempotency-Key` never creates a duplicate. | `PostgresBlipTests` (integration tests) |
| Concurrent update of the same row | 409 `concurrency_conflict` (optimistic concurrency on `xmin`). | `tests/Acme.Service.BuildingBlocks.Tests/Persistence/EfUnitOfWorkTests.cs` |
| Unique constraint violation | 409 `unique_violation`. | `tests/Acme.Service.BuildingBlocks.Tests/Persistence/EfUnitOfWorkTests.cs` |
| The app starts before `migrate` ran | The web host never migrates (unless `Migrations:RunOnStart=true`, where the container migrates first and exits 1 before any port opens on failure); `/health/ready` is 503 while any module has pending migrations, so no traffic is routed to it. | `tests/Acme.Service.IntegrationTests/ReadinessTests.cs` |
| Two `migrate` runs at once (two Jobs, or compose and a manual run) | `MigrationRunner` holds the PostgreSQL advisory lock `migrate` for the whole run, including the Hangfire schema step and the seeder, so runs are serialised. The seeder also takes its own transaction-level advisory lock. | `tests/Acme.Service.BuildingBlocks.Tests/Persistence/MigrationRunnerTests.cs` |
| Connection-string password in logs | Never logged; options validation messages name keys, not values. | |
| Identity tables migrated with default PascalCase names | The Identity model is mapped by hand on `AuthDbContext` with snake_case tables, columns and index names in the `auth` schema, so the generated migration uses snake_case names throughout. | `tests/Acme.Service.Modules.Auth.Tests/Seeding/AuthMigrationTests.cs` |
| A module touches another module's schema | Forbidden (INV-06); the architecture tests check that every context maps to its own schema only. | `tests/Acme.Service.ArchitectureTests/PersistenceConfinementTests.cs` |

Note: the plan asks for "queries retry, commands return 503". The command half holds. Queries do retry, but when every retry fails the query surfaces as a 500 rather than a 503, because exhausted query retries are not mapped to `ErrorType.Unavailable`.

## Messaging

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Business change rolls back | The outbox row rolls back with it; no event is published (INV-08). | `tests/Acme.Service.BuildingBlocks.Tests/Messaging/OutboxInboxTests.cs` |
| Two dispatchers (replicas) poll at once | `FOR UPDATE SKIP LOCKED` gives each row to one dispatcher; each message reaches a handler once per successful dispatch. | `tests/Acme.Service.BuildingBlocks.Tests/Messaging/OutboxInboxTests.cs` |
| Crash after the handler committed but before `processed_at` | The message is redelivered; the inbox row makes the handler skip it, so its side effect happens once. | `tests/Acme.Service.BuildingBlocks.Tests/Messaging/OutboxInboxTests.cs` |
| A handler keeps failing | Exponential backoff (2^attempts seconds, capped at 5 minutes), then dead at `Outbox:MaxAttempts` with an Error log and `outbox.dead`. | `tests/Acme.Service.BuildingBlocks.Tests/Messaging/OutboxInboxTests.cs` |
| One of two consumers fails | The message is retried; the consumer that already succeeded is skipped by its own inbox row. | `tests/Acme.Service.BuildingBlocks.Tests/Messaging/OutboxInboxTests.cs` |
| Unknown event type or unreadable payload (poison message) | Dead immediately; the rest of the batch continues (per-row try/catch). The payload is never written to `last_error` or logs. | `tests/Acme.Service.BuildingBlocks.Tests/Messaging/OutboxInboxTests.cs` |
| Dispatcher cannot reach its outbox (database down, schema missing) | Backs off (doubling up to a cap) and logs a Warning at most once per minute; resumes on its own. | |
| A handler performs external I/O | Forbidden inside the consumer transaction (INV-10); handlers enqueue a job that does the I/O. | Review |
| Duplicate event names | Startup fails when two event types claim the same `[IntegrationEvent]` name; the architecture tests also check names. | `tests/Acme.Service.BuildingBlocks.Tests/Messaging/IntegrationEventTypeRegistryTests.cs` |

## Jobs and locks

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Several replicas register the same recurring job | Providers keep one recurring job per id; Hangfire enqueues once per tick. Long-running recurring jobs also take an `IDistributedLock` so runs never overlap. | `tests/Acme.Service.Infra.Tests/Hangfire/HangfireMultiServerTests.cs` |
| A job throws | Hangfire retries with its default policy. The in-memory queue logs the error and retries 3 times (4 attempts), then drops the job with an Error log; the host never crashes. | `tests/Acme.Service.Testing/Contracts/JobQueueContractTests.cs` |
| The in-memory job queue restarts | Queued and scheduled jobs are lost. Development and tests only; refused in `Production`. | |
| Hangfire schema missing when the app starts | The app does not create it and stays ready; Hangfire workers log errors and `IJobQueue` calls fail until `migrate` installs the schema. The cleanup scheduler logs and retries later. | `tests/Acme.Service.IntegrationTests/HangfireSchemaOwnershipTests.cs` |
| Scheduled or recurring Hangfire jobs | Run up to about 15 seconds late (polling), never early. | |
| A job or payload type is renamed while jobs are persisted | Persisted Hangfire jobs refer to the old CLR names and fail to load. Drain the queues before renaming (including before running `scripts/init.cs` on a service with persisted jobs). | |
| Distributed lock holder dies | PostgreSQL advisory locks live on a dedicated connection and are released when it closes; the lock is also released on dispose. | `tests/Acme.Service.BuildingBlocks.Tests/Messaging/PostgresDistributedLockTests.cs` |

## Cache (Redis)

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Redis down at startup | The app starts; `/health/live` is 200 and `/health/ready` is 200 with Redis `Degraded`. | `tests/Acme.Service.Infra.Tests/Redis/RedisDownTests.cs` |
| Redis down while running | `GetAsync` is a miss, `SetAsync` and `RemoveAsync` are swallowed with a rate-limited warning, `TryAddAsync` throws so idempotency applies its fail mode, `IsAvailable` is false. Each call waits at most the connect timeout (default 500 ms). | `tests/Acme.Service.Infra.Tests/Redis/RedisDownTests.cs`, `RedisDownTests` (integration tests) |
| Redis flapping | A circuit breaker opens after 5 consecutive failures for 30 seconds, then allows one trial call. | `tests/Acme.Service.Infra.Tests/Redis/RedisCircuitBreakerTests.cs` |
| Value larger than `Cache:Redis:MaxValueBytes` (1 MiB) | `ArgumentException`; nothing is written. | |
| Polymorphic or undeserializable values | Not supported: values are serialised with System.Text.Json for the declared type; a stored value that cannot be read is a miss. | |
| Auth while Redis is down | No effect. Token validation, permissions, rate limits and the `live-session` policy never use the cache. | `RedisDownTests` (integration tests) |

## Blob storage

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Path traversal in a blob key (`../../etc/passwd`) | Rejected by `BlobKey` validation; the local store also re-checks that the canonical path stays under its root and refuses symlinks. | `tests/Acme.Service.BuildingBlocks.Tests/Blob/BlobKeyTests.cs`, `tests/Acme.Service.Infra.Tests/InMemory/LocalDiskBlobStoreSecurityTests.cs` |
| Bucket or container missing | Fails at first use with an error naming the configuration key, never the credentials. Created automatically only when `CreateIfMissing=true`. A 403 on the existence check is treated as "exists" with a warning. | `tests/Acme.Service.Infra.Tests/S3/S3BlobStoreContractTests.cs` |
| Large uploads | Streamed, never buffered whole; Azure uploads in 8 MiB blocks, 4 in flight. | `tests/Acme.Service.Infra.Tests/S3/S3BlobStoreContractTests.cs` |
| Transient 5xx from the store | The cloud SDKs' default retry policies apply. | |
| Download URL expiry too long | Capped at 15 minutes; zero or negative expiry throws. Providers without URLs (local disk) return null and the caller streams. | `tests/Acme.Service.Infra.Tests/S3/S3PresignTests.cs` |
| Local blob provider with a read-only root filesystem | Cannot happen in a deployed (`Production`) environment: `Blob:Provider=Local` is refused in `Production`. | |

## Email

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| SMTP server down | Email is always sent from a job, so the job fails and is retried by the job provider; the user can also request a new confirmation email. | `tests/Acme.Service.Infra.Tests/Smtp/SmtpNotificationSenderTests.cs` |
| SMTP misconfiguration | Startup fails naming the keys; credentials are rejected unless `UseStartTls=true`. | `tests/Acme.Service.Infra.Tests/Smtp/SmtpRegistrationTests.cs` |
| Duplicate delivery of an email event | The inbox deduplicates the handler; a job retry after a successful send can still send a second email (accepted; links stay valid). | |

## Authentication and sessions

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Tampered signature, `alg=none`, HS256 key confusion, wrong `typ`, `iss` or `aud`, expired, `nbf` in the future, unknown `kid` | 401. | `tests/Acme.Service.IntegrationTests/Security/AuthNegativeTests.cs`, `tests/Acme.Service.Modules.Auth.Tests/Tokens/TokenValidationTests.cs` |
| MFA token or refresh token used as a bearer token | 401 (INV-14). | `tests/Acme.Service.IntegrationTests/Security/AuthNegativeTests.cs` |
| Auth database down | Token validation continues statelessly, so endpoints without `live-session` keep working. `live-session` endpoints answer 503 `session_store_unavailable` and change nothing. Refresh answers 503. | `tests/Acme.Service.IntegrationTests/Resilience/AuthDbDownTests.cs`, `tests/Acme.Service.Modules.Auth.Tests/Tokens/AuthStoreUnavailableTests.cs` |
| Refresh token reused, or two parallel refreshes with one token | Exactly one refresh can succeed; any reuse revokes the whole session and logs `RefreshTokenReuse`. A client that refreshes concurrently is logged out. | `tests/Acme.Service.Modules.Auth.Tests/Tokens/RefreshTokenTests.cs` |
| Logout or role change while access tokens are outstanding | Access tokens remain valid until they expire (at most 15 minutes); `live-session` endpoints reject them at once. For urgent revocation, lock the user, which revokes all sessions. | `tests/Acme.Service.Modules.Auth.Tests/Account/LoginSecurityTests.cs` |
| Key rotation with replicas on mixed configurations | Safe only if the new key is published as `Next` on every replica before any replica makes it `Active`; see [security model](security-model.md). Keys load at startup, so each step needs a rolling restart. | |
| Clock skew between replicas | Tolerated up to `Auth:Jwt:ClockSkew` (30 seconds). | |
| Data Protection key ring empty on a new replica | Keys are shared through `auth.data_protection_keys`, so data protected on one replica is readable on another. | `tests/Acme.Service.Modules.Auth.Tests/DataProtection/SharedKeyRingTests.cs` |
| Email enumeration by response or timing | Generic 202 and 401 responses and a dummy password hash for unknown users. A small timing gap from database writes remains (accepted). | `tests/Acme.Service.Modules.Auth.Tests/Account/LoginSecurityTests.cs` |
| Password brute force | Identity lockout after 5 failures for 15 minutes (global) plus per-IP rate limits (per replica). | `tests/Acme.Service.Modules.Auth.Tests/Account/LoginSecurityTests.cs`, `tests/Acme.Service.Modules.Auth.Tests/Account/RateLimitingTests.cs` |
| Rate limiting behind a misconfigured proxy | All clients share the proxy's partition, so the limit becomes global; the forwarded-headers warning at startup points at the cause. | |
| MFA brute force or TOTP replay | A challenge allows fewer than 5 attempts, failures count towards lockout, login/mfa is rate limited, and a TOTP step already accepted for the user is rejected. | `tests/Acme.Service.Modules.Auth.Tests/Mfa/MfaLoginTests.cs` |
| Reset or confirmation token leakage | Tokens are generated at send time, never stored in the outbox and never logged; URLs containing tokens are never logged. | `tests/Acme.Service.Modules.Auth.Tests/Password/SensitiveDataLoggingTests.cs` |
| Recovery codes exposed later | Shown once at generation; stored as SHA-256 hashes and redeemable once. | `tests/Acme.Service.Modules.Auth.Tests/Persistence/AuthUserStoreTests.cs` |
| Removing the last Admin | 409 `last_admin`; locked admins do not count. | `tests/Acme.Service.Modules.Auth.Tests/Admin/AdminEndpointsTests.cs` |
| Seeder run concurrently | Serialised by the migrate lock and a transaction-level advisory lock; the result is idempotent. | `tests/Acme.Service.Modules.Auth.Tests/Seeding/AuthSeederTests.cs` |

## External OIDC login

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Open redirect through `returnUrl` | Exact match against `Auth:External:Oidc:AllowedReturnUrls`, else 400 `invalid_return_url`. | `tests/Acme.Service.Modules.Auth.Tests/External/ExternalLoginTests.cs` |
| Account takeover through a malicious identity provider | No automatic linking: an existing local account with the same email gets `error=account_exists`. | `tests/Acme.Service.Modules.Auth.Tests/External/ExternalLoginTests.cs` |
| Exchange code replay | Single use, stored hashed, expires after 60 seconds; then 401 `invalid_exchange_code`. | `tests/Acme.Service.Modules.Auth.Tests/External/ExternalLoginTests.cs` |
| Identity provider down | Only external login fails (`external_provider_unavailable`); password login is unaffected. | `tests/Acme.Service.Modules.Auth.Tests/External/ExternalLoginTests.cs` |
| External cookie and cross-site redirects | The correlation and external cookies need HTTPS (`SameSite=None; Secure`); not verified against a real provider in the template's tests. | |
| Two first logins with the same external email at once | One provisioning wins; the other can fail with a 500 from the unique index (known limitation). | |

<!-- <sample:orders> -->

## Orders sample

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Duplicate delivery of `OrderPlacedV1` or `UserRegisteredV1` | The inbox deduplicates; the customer snapshot ignores events older than its `updated_at`. | `tests/Acme.Service.Modules.Orders.Tests/Messaging/CustomerSnapshotTests.cs` |
| The expiry job runs on several replicas | `IDistributedLock("orders:expire-stale")` lets one run; orders expire in batches of 100, each in its own unit of work. | `tests/Acme.Service.Modules.Orders.Tests/Jobs/ExpireStalePendingOrdersJobTests.cs` |
| Blob written but the metadata save fails | The blob is deleted again (best effort, logged). If that delete fails or the process dies, an orphan blob remains; there is no sweep job yet. | `tests/Acme.Service.Modules.Orders.Tests/Unit/UploadAttachmentUseCaseTests.cs` |
| Metadata saved but the blob write fails | Cannot happen: the blob is written first, outside the transaction (INV-10). | |
| Upload with a fake content type or oversize file | Content is sniffed by magic bytes (PDF, PNG, JPEG): 400 otherwise; above `Orders:Attachments:MaxBytes`, 413 `attachment_too_large`. | `tests/Acme.Service.Modules.Orders.Tests/Endpoints/OrderAttachmentsTests.cs` |
| The customer was deleted before the notification job runs | `IUserDirectory` returns null; the email is skipped with an Information log. | `tests/Acme.Service.Modules.Orders.Tests/Unit/SendOrderPlacedNotificationJobTests.cs` |
| Another customer's order id is probed | 404 `order_not_found`, not 403, so ids cannot be confirmed. | `tests/Acme.Service.Modules.Orders.Tests/Endpoints/OrdersCrudTests.cs` |
| Concurrent edits of one order | `PUT` requires the `version` the client read; a mismatch is 409 `concurrency_conflict`. | `tests/Acme.Service.Modules.Orders.Tests/Endpoints/OrdersConcurrencyTests.cs` |
| Large lists | Indexed on `(customer_id, created_at desc)` and `status`; reads use no tracking; soft-deleted orders are excluded by a query filter; totals are `numeric(18,2)`. | `tests/Acme.Service.Modules.Orders.Tests/Endpoints/OrdersListTests.cs` |

<!-- </sample:orders> -->

## Deployment and operations

| Failure | Behaviour | Evidence |
| --- | --- | --- |
| Compose: the app starts before migration | `app` depends on `migrate` completing successfully, and readiness guards as well. The app container has no healthcheck (the chiseled image has no shell or curl); check `/health/ready` from the host. | `deploy/compose/compose.yaml` |
| Compose: a host port is already taken | Override the `*_PORT` variables in `deploy/compose/.env`; all ports bind to 127.0.0.1. | `deploy/compose/.env.example` |
| Debugging the chiseled image | No shell in the image: use `docker debug` or run the SDK image against the same configuration. | |
| Apple Silicon | Base images are multi-arch; the S3-compatible test and compose store is RustFS because MinIO images were not pullable. | |
| Rolling update with a schema change | Run the one-shot `migrate` container first and wait for it ([deployment](../operations/deployment.md)). New instances are not ready until their migrations are applied; old instances keep serving, which requires expand-and-contract migrations. | |
| The `migrate` container fails | It exits non-zero and the deploy stops before the app is started or updated. | |
| Config-only change | Environment values are read at start; redeploy or restart the instances to apply them. | |
| CI: flaky containers | The integration step is retried once and the retry is reported as a warning. | `.github/workflows/ci.yml` |
| CI: a new vulnerability in a dependency | High and critical NuGet advisories fail the build; low and moderate stay warnings. | `scripts/ci/check-vulnerable.sh` |
| CI: compromised third-party action | Every action is pinned by full commit SHA. CI uses no repository secrets. | `.github/workflows/ci.yml` |
| Architecture rule false negatives | Type-level rules are complemented by csproj parsing for project references; the illegal-reference fixture is referenced only by the architecture tests. | `tests/Acme.Service.ArchitectureTests/IllegalReferenceProofTests.cs` |
| Integration tests flaky on container timing | Readiness waits and polling with timeouts, no fixed sleeps; destructive tests own their containers. | |
| `scripts/init.cs` interrupted or misused | Refuses a dirty tree (unless `--force`), an already initialised repository and invalid or colliding names; plans every change before writing; `--dry-run` writes nothing. Run it on a fresh clone. Lock files are regenerated with `dotnet restore --force-evaluate`. | `.github/workflows/template-selftest.yml` |
| `scripts/init.cs` on a case-insensitive file system, or with a colliding name | A rename that differs only in letter case goes through a temporary name. Names that contain the template name in any case, including `--name Acme.Service` itself, are refused, so in practice that path is not reachable. | `.github/workflows/template-selftest.yml` |
| Documentation drifts from the code | `bash scripts/ci/check-doc-links.sh` fails on a broken relative link or a backticked repository path that does not exist. | `scripts/ci/check-doc-links.sh` |
