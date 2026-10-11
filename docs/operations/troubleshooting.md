# Troubleshooting

> Applies to: Acme.Service template

Symptoms, causes and fixes for the problems operators and developers meet most often. Expected behaviour under every fault is catalogued in [failure modes](../architecture/failure-modes.md); probes, metrics and alerts are in [observability](../architecture/observability.md).

## The process exits at startup

The service validates all configuration before it serves anything and exits with code 1. The log line starts with `Invalid configuration:` and names every failing key, for example:

- `Blob:Provider is required. Registered providers: AzureBlob, Local, S3.` A category has no provider (the base `appsettings.json` sets none for Blob, Notifications and Jobs).
- `Notifications:Provider=Console is a development-only provider and is not allowed in the Production environment (D-21).`
- `Auth:Jwt:SigningKeys must contain exactly one key with Status=Active (found 0).` No key ring: run `dotnet run scripts/dev-env.cs` locally, or check the `acme-service-jwt-keys` Secret and the `Auth__Jwt__SigningKeys__*` ConfigMap entries.
- `Auth:Links:ConfirmEmailUrl is required ...`: the email link URLs must be set in every environment.

Fix the named keys ([configuration](../guides/configuration.md)). In Kubernetes a missing Secret shows as `CreateContainerConfigError` before the process even starts.

### A verb fails locally with "ConnectionStrings:... is not configured"

`migrate` reports `ConnectionStrings:PostgresMigrations is not configured`; `auth:grant-role` reports `ConnectionStrings:Postgres is not configured`. The verbs build a plain .NET host that reads `DOTNET_ENVIRONMENT`, not the launch profile's `ASPNETCORE_ENVIRONMENT`, so a local `dotnet run -- migrate` (or `-- auth:grant-role ...`) runs as `Production` and ignores `appsettings.Development.json` and user secrets. Run `DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate`, or add `--environment=Development` after the verb.

## Readiness returns 503

`/health/ready` answers `Unhealthy` with 503 when PostgreSQL is unreachable or **any module context has pending migrations**. The health check's log entry says which, for example `Pending migrations in: auth. Run the migrate command.` or `Could not read the migration history.`

1. List what is pending (same image and configuration as the app):

   ```bash
   DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate --list   # local
   docker compose -f deploy/compose/compose.yaml run --rm migrate migrate --list                       # Compose
   kubectl logs job/acme-service-migrate -n <ns>                                                        # Kubernetes: did the Job run?
   ```

   In Kubernetes, `migrate` and `migrate --list` never run inside the app pods: they need `ConnectionStrings:PostgresMigrations`, which only the migration Job receives. Read the last Job's log as above (it lists every migration it applied), or, to only list pending migrations, apply a copy of `deploy/k8s/migration/base/job.yaml` under another name with `args: ["migrate", "--list"]` through the same overlay.

2. Run the migration: locally `migrate`; in Compose `docker compose -f deploy/compose/compose.yaml run --rm migrate`; in Kubernetes the Job, in the apply order of [deployment](deployment.md).
3. If nothing is pending, check connectivity and credentials of `ConnectionStrings:Postgres` (the PostgreSQL check times out after 5 seconds). `Could not read the migration history.` with a permission error means the application role lacks `USAGE` on a module schema or `SELECT` on its `__ef_migrations_history` table; every pod then stays not ready ([database roles](security-hardening.md#per-schema-database-roles)).

The pending-migrations result is cached for 30 seconds per pod, the "pending" answer included; only a failure to read the migration history (an exception) is not cached. After `migrate` finishes, readiness can therefore stay 503 for up to 30 seconds plus one probe period (10 seconds in the base manifests) before it turns green. A new pod that is not ready never receives traffic, and with `maxUnavailable: 0` the old pods keep serving.

## Redis is down: readiness says Degraded

With `Cache:Provider=Redis`, a Redis outage makes the `redis` readiness check `Degraded`. Readiness still returns **200**, so pods stay in rotation (D-20). While Redis is down:

- cache reads are misses and writes are skipped, with rate-limited warnings; each call waits at most `Cache:Redis:ConnectTimeout` (500 ms), and a circuit breaker opens after repeated failures;
- requests with an `Idempotency-Key` header fail with 503 `idempotency_unavailable` when `Idempotency:FailMode=Closed` (the default); without the header they work normally;
- authentication, refresh, the live-session checks and rate limiting are unaffected (none uses Redis).

The readiness probe logs a warning every time it runs during the outage. Restore Redis; the client reconnects on its own and the breaker closes. If you would rather accept duplicate POSTs than reject them during a cache outage, set `Idempotency:FailMode=Open`.

## Dead outbox messages

A message is dead when it failed `Outbox:MaxAttempts` (10) times, or at once when its event type is not registered or its payload cannot be read. Signs: an Error log `Outbox message <id> (<type>) of module <module> is dead ...` and an increase of the `outbox.dead` metric. Dead rows are never deleted automatically.

Each module has its own `outbox_messages` table in its schema: `auth.outbox_messages`, plus one per added module.

<!-- <sample:orders> -->

The Orders sample's table is `orders.outbox_messages`; use it in place of `auth` in the statements below for Orders events.

<!-- </sample:orders> -->

Inspect:

```sql
SELECT id, type, attempts, occurred_at, dead_at, last_error, correlation_id
FROM auth.outbox_messages
WHERE dead_at IS NOT NULL
ORDER BY dead_at DESC;
```

`last_error` holds the exception type and message of the last failure (never the payload); `correlation_id` finds the original request in the logs.

Fix the cause first: a bug in a consumer (deploy the fix), an unavailable dependency, or, for "unknown integration event type", a deployment that dropped or renamed the event type (restore the `[IntegrationEvent]` name). Then replay:

```sql
-- one message
UPDATE auth.outbox_messages
SET dead_at = NULL, attempts = 0, next_attempt_at = now(), last_error = NULL
WHERE id = '<message id>' AND dead_at IS NOT NULL;

-- every dead message of one event type
UPDATE auth.outbox_messages
SET dead_at = NULL, attempts = 0, next_attempt_at = now(), last_error = NULL
WHERE dead_at IS NOT NULL AND type = '<event name, for example auth.user-registered.v1>';
```

The dispatcher picks the rows up within `Outbox:PollingInterval`. Replay is safe **while the consumers' inbox rows still exist**: consumers that already processed the message are skipped by those rows, so only the ones that failed run again. Inbox rows are deleted after `Inbox:RetentionDays` (30), but dead outbox rows are kept forever. Before replaying, check `occurred_at` and `dead_at`: for a message older than the inbox retention, a consumer that processed it before may process it again, so check that every consumer of that event type tolerates the duplicate (or repair the failed consumer's data by hand instead of replaying). To discard a message instead, `DELETE FROM auth.outbox_messages WHERE id = '<message id>' AND dead_at IS NOT NULL;`. Run these statements with a role that has DML rights on the schema, and record what you replayed or deleted.

## The Hangfire dashboard in development

The dashboard is mapped at `/hangfire` only when `Jobs:Provider=Hangfire`, `Jobs:Hangfire:DashboardEnabled=true` **and** the environment is `Development`, and it accepts local requests only. The Compose stack runs as `Production`, so use a local run:

```bash
docker compose -f deploy/compose/compose.yaml up -d postgres
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate --Jobs:Provider=Hangfire
Jobs__Provider=Hangfire Jobs__Hangfire__DashboardEnabled=true dotnet run --project src/Host/Acme.Service.Host
# open http://localhost:8080/hangfire
```

The first `migrate` with `Jobs:Provider=Hangfire` creates the `hangfire` schema. Outside Development the setting is ignored and a warning is logged; there is no production dashboard. To inspect jobs in a deployed environment, query the `hangfire` schema read-only or add an authenticated dashboard yourself.

## Jobs do not run

- **Hangfire schema missing.** The app never creates it. Workers log errors and `IJobQueue` calls fail until a `migrate` with `Jobs:Provider=Hangfire` has run. The migration overlays and the Compose `migrate` service already set it.
- **Late jobs.** Scheduled and recurring Hangfire jobs are polled every 15 seconds.
- **Failed jobs.** Hangfire retries 10 times with increasing delays and then marks the job Failed. The in-memory provider retries 3 times, then logs an Error and drops the job.
- **Renamed job or payload type.** Persisted Hangfire jobs refer to CLR type names and fail to load after a rename. Drain queues before renaming.

## Emails do not arrive

- Development (`Notifications:Provider=Console`): nothing is sent; look for `Email (not sent; console provider)` in the log, which includes the body with the link in Development only.
- Compose: every email lands in Mailpit at `http://localhost:8025`.
- SMTP: an SMTP failure fails the job, which retries; the source event stays processed in the outbox. Check host, port, `UseStartTls` (credentials require it) and `From`.
- Register, resend-confirmation and forgot-password always return 202, also when no email is sent (unknown or already confirmed account), by design.

## 401, 403 and 503 from the API

| Response | Usual cause |
| --- | --- |
| 401 on every route, including unknown ones | No or invalid access token. Unknown routes are 401 too, because the fallback policy requires authentication before routing can return 404. |
| 401 with a token that looks valid | Expired (15 minutes), wrong issuer or audience (`Auth:Jwt:Issuer`, `Auth:Jwt:Audience` differ between issuer and validator), a `kid` not in this pod's key ring (rotation done out of order, see [key rotation](key-rotation.md)), an MFA token used as an access token, or clock skew beyond `Auth:Jwt:ClockSkew`. |
| 401 `session_revoked` | The session behind the token was revoked (logout, logout-all, password change or reset, lock) and the endpoint requires a live session. |
| 403 | The token lacks the required `perm` claim. Permissions are copied into the token at issue time: refresh after a role change, and make sure `migrate` ran after a new permission was added. |
| 403 `email_not_verified` | Correct password, unconfirmed email. |
| 429 with `Retry-After` | Auth rate limits (per IP and per replica). If every client is limited together, the forwarded headers are not trusted (next section). |
| 503 `session_store_unavailable` | A live-session endpoint could not reach the Auth database; it fails closed. Endpoints without the policy keep working. |
| 503 `database_unavailable` with `Retry-After` | A transient database fault in a command; retry, preferably with an `Idempotency-Key`. |

Every problem response carries `traceId` and `correlationId`; search the logs for them.

## All clients share one rate limit, or logs show the proxy's IP

The startup warning `Http:ForwardedHeaders has no KnownNetworks or KnownProxies ...` means `X-Forwarded-For` is trusted from loopback only. Behind an ingress, every request then appears to come from the ingress, so per-IP rate limits apply to everyone at once and client IPs in logs are wrong. Set `Http__ForwardedHeaders__KnownNetworks__0` to the network your ingress or load balancer connects from ([security hardening](security-hardening.md#forwarded-headers)).

## Debugging the container

The runtime image is chiseled: no shell, no package manager, no curl. Use:

- `kubectl logs` and the OTLP export for diagnosis;
- `kubectl debug -it <pod> -n <ns> --image=busybox --target=acme-service` for an ephemeral debugging container in the pod's namespaces; in namespaces enforcing the Pod Security `restricted` profile add `--profile=restricted`, otherwise the ephemeral container is rejected;
- `kubectl exec <pod> -n <ns> -- dotnet Acme.Service.Host.dll auth:grant-role --email <email> --role <role>` is the only verb that runs in an app pod (it uses `ConnectionStrings:Postgres`); `migrate` and `migrate --list` need the migration Job's connection string (see [readiness returns 503](#readiness-returns-503)), and `kubectl exec` shares the pod's memory limit ([deployment](deployment.md#after-the-first-deployment));
- locally, `docker compose -f deploy/compose/compose.yaml logs -f app` and `docker compose -f deploy/compose/compose.yaml ps` (the `migrate` service should show `Exited (0)`).

## Port already in use (Compose)

Every published port has a variable in `deploy/compose/.env`: `APP_PORT`, `POSTGRES_PORT`, `REDIS_PORT`, `S3_PORT`, `AZURITE_BLOB_PORT`, `MAILPIT_UI_PORT`, `MAILPIT_SMTP_PORT`, `ASPIRE_UI_PORT` and `ASPIRE_OTLP_PORT`. Change the clashing one and run `docker compose -f deploy/compose/compose.yaml up -d` again. With a non-default `POSTGRES_PORT`, also adjust the local connection strings in user secrets.
