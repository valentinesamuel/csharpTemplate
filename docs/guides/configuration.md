# Configuration reference

> Applies to: Acme.Service template

Every setting the service reads, its environment-variable form, its default and whether it is a secret. Options are validated when the process starts (INV-17): a missing or malformed value stops startup with exit code 1 and a log line that names the full key.

`bash scripts/ci/check-config-keys.sh` fails when a key from the reference list kept in that script is missing from this page.

To add a new setting, follow [adding an environment variable](adding-an-environment-variable.md).

## How configuration is loaded

Sources, later ones winning:

1. `src/Host/Acme.Service.Host/appsettings.json` (safe defaults; no secrets).
2. `src/Host/Acme.Service.Host/appsettings.Development.json` when the environment is `Development` (local providers, a local connection string without a password, OpenAPI on).
3. User secrets (`UserSecretsId` `acme-service`) when the environment is `Development`.
4. Environment variables. Replace every `:` with `__` (two underscores): `Auth:Jwt:Issuer` becomes `Auth__Jwt__Issuer`. Array entries use their index: `Auth__Jwt__SigningKeys__0__Kid`.
5. Command-line arguments. The `migrate` and `auth:grant-role` verbs accept only `--Section:Key=value` overrides, for example `--ConnectionStrings:PostgresMigrations=...`.

The environment comes from `ASPNETCORE_ENVIRONMENT` for the web app. The two verbs build a plain .NET host, which reads `DOTNET_ENVIRONMENT` (or a `--environment=Development` argument) instead and ignores `ASPNETCORE_ENVIRONMENT`. That matters for local runs: `dotnet run --project src/Host/Acme.Service.Host -- migrate` uses the launch profile, which sets only `ASPNETCORE_ENVIRONMENT`, so the verb runs as `Production` and does not see `appsettings.Development.json` or user secrets. Set `DOTNET_ENVIRONMENT=Development` or pass `--environment=Development` (see [getting started](getting-started.md)).

Rules for secrets (INV-11): never put a secret in an `appsettings*.json` file, a deployment manifest, a build argument or the image. Supply it as an environment variable from the platform's environment, a mounted file, or (locally) user secrets and the gitignored `deploy/compose/.env`. When deployed, secrets are `sync: false` environment variables on the Render service, entered once in the dashboard; see [secrets](../operations/secrets.md).

In the tables, **Secret** means the value must come from a secret source. "Sensitive" means it is not a credential by itself but should not be published (for example a user name).

## Connection strings

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | empty; Development: `Host=localhost;Port=5432;Database=acme_service;Username=postgres` | Yes (contains the password) | Used by the web app for every module context (DML only), by Hangfire storage and by `auth:grant-role`. A missing value fails when a module context is first resolved, and `/health/ready` returns 503. |
| `ConnectionStrings:PostgresMigrations` | `ConnectionStrings__PostgresMigrations` | empty; Development: same as above | Yes | Used only by `migrate` (all module contexts, the Hangfire schema step and the seeder) and by `dotnet ef` design-time tooling, which reads the environment variable. Give it a role allowed to run DDL. In development both point to the same database. |

## Migrations

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Migrations:RunOnStart` | `Migrations__RunOnStart` | `false` | No | When `true`, the container runs the `migrate` code path first (advisory lock, module migrations, Hangfire schema, Auth seed) and starts the web app only if it succeeded; on failure it exits non-zero before any port opens. Requires `ConnectionStrings:PostgresMigrations`, which is unset in-process afterwards. Set on Render in `render.yaml`; keep `false` locally, where Compose runs the one-shot `migrate` service. |

## Provider selection

Each category selects exactly one registered provider; names match case-insensitively. A missing, unknown or ambiguous value stops startup, and so does a development-only provider when the environment is `Production` (D-21). See [adding a provider](adding-a-provider.md).

| Key | Environment variable | Default | Values | Notes |
| --- | --- | --- | --- | --- |
| `Cache:Provider` | `Cache__Provider` | `InMemory` | `InMemory`, `Redis` | `InMemory` is allowed in Production but logs a warning because idempotency state is then per replica. |
| `Blob:Provider` | `Blob__Provider` | empty; Development: `Local` | `Local` (development only), `S3`, `AzureBlob` | Required. |
| `Notifications:Provider` | `Notifications__Provider` | empty; Development: `Console` | `Console` (development only), `Smtp` | Required. |
| `Jobs:Provider` | `Jobs__Provider` | empty; Development: `InMemory` | `InMemory` (development only), `Hangfire` | Required. `migrate` reads it too: with `Hangfire` it installs the Hangfire schema. |
| `Locks:Provider` | `Locks__Provider` | `Postgres` | `InMemory` (development only), `Postgres` | PostgreSQL advisory locks on a dedicated connection. |

### Cache

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Cache:Redis:ConnectionString` | `Cache__Redis__ConnectionString` | none | Yes (usually contains the password) | Required when `Cache:Provider=Redis`. StackExchange.Redis format, for example `<host>:6379,password=<password>,ssl=true`. |
| `Cache:Redis:ConnectTimeout` | `Cache__Redis__ConnectTimeout` | `00:00:00.500` | No | Longest a cache call waits for a connection. Greater than zero, at most one minute. |
| `Cache:Redis:SyncTimeout` | `Cache__Redis__SyncTimeout` | `00:00:00.500` | No | Per-operation timeout. Greater than zero, at most one minute. |
| `Cache:Redis:MaxValueBytes` | `Cache__Redis__MaxValueBytes` | `1048576` | No | Largest serialized value accepted. |
| `Cache:DefaultTtl` | `Cache__DefaultTtl` | none | No | Listed in the plan's configuration reference, but **not read by any code**: every `ICache` call passes its own TTL. Setting it has no effect. |

### Blob storage

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Blob:Local:RootPath` | `Blob__Local__RootPath` | Development: `.data/blobs` | No | Required for `Local`. Relative paths resolve against the content root. Development only. |
| `Blob:S3:ServiceUrl` | `Blob__S3__ServiceUrl` | none | No | Endpoint of an S3-compatible store (Cloudflare R2, MinIO, RustFS). Leave empty for AWS S3. At least one of `ServiceUrl` and `Region` is required. |
| `Blob:S3:Region` | `Blob__S3__Region` | none | No | AWS region, or the signing region of an S3-compatible store (R2 uses `auto`). |
| `Blob:S3:Bucket` | `Blob__S3__Bucket` | none | No | Required. A valid S3 bucket name. |
| `Blob:S3:AccessKey` | `Blob__S3__AccessKey` | none | Yes | Set together with `SecretKey`, or leave both empty to use the default AWS credential chain (IRSA, instance role, environment). |
| `Blob:S3:SecretKey` | `Blob__S3__SecretKey` | none | Yes | See `AccessKey`. |
| `Blob:S3:ForcePathStyle` | `Blob__S3__ForcePathStyle` | `false` | No | `true` for stores that need path-style addressing (RustFS, MinIO). |
| `Blob:S3:CreateIfMissing` | `Blob__S3__CreateIfMissing` | `false` | No | Not in the plan's reference; creates the bucket on first use. Otherwise a missing bucket fails at first use. |
| `Blob:AzureBlob:ConnectionString` | `Blob__AzureBlob__ConnectionString` | none | Yes | Shared-key authentication. Exactly one of `ConnectionString` and `ServiceUri` is required. |
| `Blob:AzureBlob:ServiceUri` | `Blob__AzureBlob__ServiceUri` | none | No | `https://<account>.blob.core.windows.net` with Microsoft Entra ID through `DefaultAzureCredential` (workload or managed identity). |
| `Blob:AzureBlob:Container` | `Blob__AzureBlob__Container` | none | No | Required. A valid container name. |
| `Blob:AzureBlob:CreateIfMissing` | `Blob__AzureBlob__CreateIfMissing` | `false` | No | Not in the plan's reference; creates the container on first use. |

### Email notifications

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Notifications:Smtp:Host` | `Notifications__Smtp__Host` | none | No | Required for `Smtp`. Host name or IP address. |
| `Notifications:Smtp:Port` | `Notifications__Smtp__Port` | `587` | No | 1 to 65535. |
| `Notifications:Smtp:UseStartTls` | `Notifications__Smtp__UseStartTls` | `true` | No | `true` requires STARTTLS. Credentials are rejected at startup when this is `false`, so they are never sent in clear text. |
| `Notifications:Smtp:Username` | `Notifications__Smtp__Username` | none | Sensitive | Set together with `Password`, or leave both empty for no authentication. |
| `Notifications:Smtp:Password` | `Notifications__Smtp__Password` | none | Yes | See `Username`. |
| `Notifications:Smtp:From` | `Notifications__Smtp__From` | none | No | Required. A mailbox such as `no-reply@example.com` or `Service <no-reply@example.com>`. |

The SMTP timeout (30 seconds) is a code default with no configuration key.

### Background jobs

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Jobs:Hangfire:Schema` | `Jobs__Hangfire__Schema` | `hangfire` | No | Lower-case PostgreSQL identifier. Must be the same for `migrate` and the app. |
| `Jobs:Hangfire:WorkerCount` | `Jobs__Hangfire__WorkerCount` | processor count x 5, at most 20 | No | Workers per replica, 1 to 1000. |
| `Jobs:Hangfire:DashboardEnabled` | `Jobs__Hangfire__DashboardEnabled` | `false` | No | Maps `/hangfire` only when the environment is also `Development`; ignored with a warning elsewhere. |

Hangfire uses `ConnectionStrings:Postgres` at run time and `ConnectionStrings:PostgresMigrations` for the schema install. Queue and schedule polling (15 seconds) are code defaults.

## Messaging and idempotency

These are code defaults; set a key only to override it.

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Outbox:PollingInterval` | `Outbox__PollingInterval` | `00:00:02` | No | Wait between dispatch polls when a batch was not full. Greater than zero. |
| `Outbox:BatchSize` | `Outbox__BatchSize` | `50` | No | Rows locked per poll, 1 to 1000. |
| `Outbox:MaxAttempts` | `Outbox__MaxAttempts` | `10` | No | Failed deliveries before a message is dead. At least 1. |
| `Outbox:RetentionDays` | `Outbox__RetentionDays` | `7` | No | Processed outbox rows older than this are deleted hourly. Dead rows are never deleted automatically. |
| `Inbox:RetentionDays` | `Inbox__RetentionDays` | `30` | No | Inbox rows older than this are deleted hourly. Keep it well above the longest redelivery window. |
| `Idempotency:Ttl` | `Idempotency__Ttl` | `1.00:00:00` | No | How long a completed response is replayed for the same `Idempotency-Key`. |
| `Idempotency:InProgressTtl` | `Idempotency__InProgressTtl` | `00:01:00` | No | Lifetime of the in-progress marker; at most `Ttl`. |
| `Idempotency:FailMode` | `Idempotency__FailMode` | `Closed` | No | `Closed`: 503 `idempotency_unavailable` when the cache is down. `Open`: process without idempotency and log a warning. |

## Auth

### Tokens and signing keys

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Auth:Jwt:Issuer` | `Auth__Jwt__Issuer` | `acme-service` | No | Required. `iss` of issued tokens and the only accepted issuer. |
| `Auth:Jwt:Audience` | `Auth__Jwt__Audience` | `acme-service` | No | Required. `aud` of access tokens; MFA tokens use `<Audience>:mfa`. |
| `Auth:Jwt:AccessTokenLifetime` | `Auth__Jwt__AccessTokenLifetime` | `00:15:00` | No | Also the upper bound on permission staleness. |
| `Auth:Jwt:RefreshTokenLifetime` | `Auth__Jwt__RefreshTokenLifetime` | `14.00:00:00` | No | Bounded by the session's absolute expiry. |
| `Auth:Jwt:SessionAbsoluteLifetime` | `Auth__Jwt__SessionAbsoluteLifetime` | `30.00:00:00` | No | No refresh extends a session beyond this. |
| `Auth:Jwt:ClockSkew` | `Auth__Jwt__ClockSkew` | `00:00:30` | No | 0 to 5 minutes. |
| `Auth:Jwt:SigningKeys:<n>:Kid` | `Auth__Jwt__SigningKeys__<n>__Kid` | none | No | 1 to 64 characters of `A-Z a-z 0-9 . _ -`, unique in the ring. |
| `Auth:Jwt:SigningKeys:<n>:Status` | `Auth__Jwt__SigningKeys__<n>__Status` | none | No | `Active`, `Next` or `Retiring`. Exactly one key must be `Active`. |
| `Auth:Jwt:SigningKeys:<n>:PrivateKeyPath` | `Auth__Jwt__SigningKeys__<n>__PrivateKeyPath` | none | The file is | Path of a PEM RSA private key (at least 2048 bits), for example a file mounted by the platform. Render has no file mount for this, so deployed keys use `PrivateKeyBase64`. Exactly one of `PrivateKeyPath` and `PrivateKeyBase64` per key. |
| `Auth:Jwt:SigningKeys:<n>:PrivateKeyBase64` | `Auth__Jwt__SigningKeys__<n>__PrivateKeyBase64` | none | Yes | Base64 of the PEM text, for environment variables (Render environment variables when deployed) and user secrets. `scripts/dev-env.cs` generates one. |

There is no signing key in any `appsettings*.json` file, so the web app does not start until a key ring is configured. Keys are loaded at startup only; rotation is in [key rotation](../operations/key-rotation.md).

### Identity

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Auth:Identity:RequireConfirmedEmail` | `Auth__Identity__RequireConfirmedEmail` | `true` | No | When true, a correct password on an unconfirmed account returns 403 `email_not_verified`. |
| `Auth:Identity:Password:RequiredLength` | `Auth__Identity__Password__RequiredLength` | `12` | No | 12 to 128. No composition rules. |
| `Auth:Identity:Lockout:MaxFailedAccessAttempts` | `Auth__Identity__Lockout__MaxFailedAccessAttempts` | `5` | No | At least 1. Stored in the database, so global across replicas. |
| `Auth:Identity:Lockout:DefaultLockoutTimeSpan` | `Auth__Identity__Lockout__DefaultLockoutTimeSpan` | `00:15:00` | No | Positive. |
| `Auth:Identity:TokenLifespan` | `Auth__Identity__TokenLifespan` | `01:00:00` | No | Lifetime of email-confirmation and password-reset tokens. |

### Rate limiting

Limits are per replica (in-process); the effective limit is the configured value times the number of replicas ([ADR-0012](../adr/0012-in-process-rate-limiting.md)).

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Auth:RateLimiting:Credentials:PermitLimit` | `Auth__RateLimiting__Credentials__PermitLimit` | `10` | No | Policy `auth-credentials`: per client IP, sliding window. Login, login/mfa, refresh, reset-password, external exchange. |
| `Auth:RateLimiting:Credentials:Window` | `Auth__RateLimiting__Credentials__Window` | `00:01:00` | No | 1 second to 1 day. |
| `Auth:RateLimiting:Email:PermitLimit` | `Auth__RateLimiting__Email__PermitLimit` | `5` | No | Policy `auth-email`: per client IP and per normalized email. Register, resend-confirmation, forgot-password. |
| `Auth:RateLimiting:Email:Window` | `Auth__RateLimiting__Email__Window` | `00:01:00` | No | 1 second to 1 day. |

### Links, roles and MFA

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Auth:Links:ConfirmEmailUrl` | `Auth__Links__ConfirmEmailUrl` | Development: `http://localhost:3000/confirm-email` | No | Required in every environment. Absolute front-end URL; the email link appends `userId` and `token` query parameters. |
| `Auth:Links:ResetPasswordUrl` | `Auth__Links__ResetPasswordUrl` | Development: `http://localhost:3000/reset-password` | No | Required in every environment. Same query parameters. |
| `Auth:DefaultRole` | `Auth__DefaultRole` | `Customer` | No | Role given to self-registered and provisioned users. Must be a seed role (`Admin`, `Staff`, `Customer`). |
| `Auth:Mfa:Issuer` | `Auth__Mfa__Issuer` | `acme-service` | No | Issuer shown by authenticator apps. 1 to 64 characters, no `:`. |

### External OIDC login

Validated only when `Enabled` is true. See [auth](auth.md#external-oidc-login).

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Auth:External:Oidc:Enabled` | `Auth__External__Oidc__Enabled` | `false` | No | When false the external routes are not mapped. |
| `Auth:External:Oidc:DisplayName` | `Auth__External__Oidc__DisplayName` | `OpenID Connect` | No | Must not be empty. |
| `Auth:External:Oidc:Authority` | `Auth__External__Oidc__Authority` | empty | No | Absolute https URL of the provider (http allowed in Development). |
| `Auth:External:Oidc:ClientId` | `Auth__External__Oidc__ClientId` | empty | No | Required when enabled. |
| `Auth:External:Oidc:ClientSecret` | `Auth__External__Oidc__ClientSecret` | empty | Yes | Required when enabled. |
| `Auth:External:Oidc:Scopes` | `Auth__External__Oidc__Scopes__<n>` | `openid`, `profile`, `email` | No | Must contain `openid`. |
| `Auth:External:Oidc:AllowedReturnUrls` | `Auth__External__Oidc__AllowedReturnUrls__<n>` | empty | No | At least one when enabled. Absolute URLs without query or fragment; `returnUrl` must match one exactly. |

## HTTP, observability and OpenAPI

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Http:ForwardedHeaders:KnownNetworks` | `Http__ForwardedHeaders__KnownNetworks__<n>` | empty | No | Trusted proxy networks in CIDR form (`10.0.0.0/8`). With no networks and no proxies only loopback is trusted, and a warning is logged outside Development. |
| `Http:ForwardedHeaders:KnownProxies` | `Http__ForwardedHeaders__KnownProxies__<n>` | empty | No | Trusted proxy IP addresses. |
| `Http:ForwardedHeaders:TrustAllProxies` | `Http__ForwardedHeaders__TrustAllProxies` | `false` | No | Trusts `X-Forwarded-For` and `X-Forwarded-Proto` from any sender, nearest hop only (`ForwardLimit` 1); overrides `KnownNetworks` and `KnownProxies`. Only safe when the platform edge is the only way to reach the service, as on Render (set to `true` in `render.yaml`). Never enable it where clients can reach the service directly: they could spoof their address. |
| `Observability:ServiceName` | `Observability__ServiceName` | `acme-service` | No | OpenTelemetry `service.name`. Required. |
| `Observability:Otlp:Endpoint` | `Observability__Otlp__Endpoint` | empty | No | http(s) OTLP endpoint for traces, metrics and logs. Empty disables export. |
| `Serilog:MinimumLevel:Default` | `Serilog__MinimumLevel__Default` | `Information`; Development: `Debug` | No | Any standard `Serilog:*` setting is honoured. |
| `Serilog:MinimumLevel:Override:<category>` | `Serilog__MinimumLevel__Override__<category>` | `Microsoft.AspNetCore`, `System.Net.Http.HttpClient` and EF Core commands at `Warning` | No | Per-category minimum level. |
| `OpenApi:Enabled` | `OpenApi__Enabled` | `false`; Development: `true` | No | Maps `/openapi/v1.json` and `/scalar`. |

Standard ASP.NET Core settings such as `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_HTTP_PORTS` (`8080` in the image) and `AllowedHosts` work as usual.

<!-- <sample:orders> -->

## Orders sample

| Key | Environment variable | Default | Secret | Notes |
| --- | --- | --- | --- | --- |
| `Orders:PendingExpiry` | `Orders__PendingExpiry` | `1.00:00:00` | No | Pending orders older than this are expired by the recurring job. |
| `Orders:Attachments:MaxBytes` | `Orders__Attachments__MaxBytes` | `10485760` | No | Largest accepted attachment; larger uploads get 413. |

<!-- </sample:orders> -->

## Where values are set per environment

| Environment | Non-secret values | Secret values |
| --- | --- | --- |
| Local `dotnet run` | `src/Host/Acme.Service.Host/appsettings.Development.json` | User secrets, printed by `dotnet run scripts/dev-env.cs` |
| Docker Compose | `deploy/compose/compose.yaml` (`environment:`) | `deploy/compose/.env`, generated by `scripts/dev-env.cs` from `deploy/compose/.env.example` |
| Deployed | Plain environment variables set by the deployment platform (the Compose `environment:` block or your platform's equivalent) | `sync: false` environment variables in `render.yaml`, values entered in the Render dashboard ([secrets](../operations/secrets.md#render-environment-variables)) |
