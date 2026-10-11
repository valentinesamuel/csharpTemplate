# Observability

> Applies to: Acme.Service template

Logs go through Serilog; traces and metrics through OpenTelemetry. Everything is set up in `src/Host/Acme.Service.Host/Observability/ObservabilityExtensions.cs` and configured by the `Observability` and `Serilog` sections.

## Configuration

| Key | Default | Effect |
| --- | --- | --- |
| `Observability:ServiceName` | `acme-service` | OpenTelemetry `service.name` for traces, metrics and OTLP logs. Required; an empty value stops startup. |
| `Observability:Otlp:Endpoint` | empty | When set to an http(s) URL, traces, metrics and logs are exported over OTLP. When empty, nothing is exported. |
| `Serilog:MinimumLevel:*` | Information, with `Microsoft.AspNetCore`, `System.Net.Http.HttpClient` and EF Core commands at Warning | Standard Serilog configuration. |

Locally, `docker compose -f deploy/compose/compose.yaml --profile observability up -d` starts an Aspire dashboard as the OTLP sink; set `OTLP_ENDPOINT` in `deploy/compose/.env` as described in `deploy/compose/compose.yaml`.

## Logs

- Console output is plain text in Development and rendered compact JSON elsewhere, ready for a log collector.
- Every request produces one summary event: method, path (never the query string), status code, duration and correlation id. Health probe requests log at Debug; 5xx and exceptions at Error. Headers are never logged, so neither is `Authorization`.
- Every log event written during a request carries `CorrelationId` from the log scope.
- The use-case pipeline logs the use-case name, outcome, error code and duration (`LoggingBehavior`, event ids 1000 to 1002). It never logs inputs.
- Security-relevant actions are logged as message templates starting with `SecurityEvent {EventType}` (for example locks, logout-all, refresh token reuse, external code exchange) with ids only.
- Use message templates, never string interpolation, and never log secrets, tokens, codes or email bodies outside Development (INV-28).

## Traces

Instrumented sources: ASP.NET Core server requests, outgoing `HttpClient` calls, Npgsql commands (native `Npgsql` activity source), and the outbox dispatch (`Acme.Service.Messaging` source, activity `outbox.dispatch`).

Context propagation:

- Inbound W3C `traceparent` is honoured; the response problem documents carry `traceId`.
- Outbox rows store the producing request's `traceparent` and correlation id, so the dispatch span and the consumer's logs link back to the request that raised the event.
- Jobs restore the correlation id that was current when they were enqueued.

## Metrics

| Meter | Examples |
| --- | --- |
| ASP.NET Core | Request duration and count by route and status, active requests, rate limiter rejections. |
| `HttpClient` | Outgoing request duration. |
| Runtime | GC, thread pool, exceptions. |
| `Npgsql` | Connection pool usage, command duration. |
| `Acme.Service.Messaging` | `outbox.dead`: messages given up after `Outbox:MaxAttempts` or because their type is unknown, tagged with `module` and `event.type`. Alert on any increase. |

## Correlation ids

`X-Correlation-Id` is accepted when it is a single value of 1 to 64 characters from `[A-Za-z0-9-_]`; otherwise a new id is generated (an invalid inbound value is never echoed). The id is returned in the response header, added to the log scope and to every problem document, stored with outbox messages and job metadata, and forwarded on outbound `HttpClient` calls.

## Health probes

Both endpoints are anonymous and excluded from OpenAPI (`src/Host/Acme.Service.Host/Health/HealthEndpoints.cs`).

| Endpoint | Checks | Use it for |
| --- | --- | --- |
| `/health/live` | The process only. | Liveness and startup probes. Never depends on a dependency, so an outage never restarts pods. |
| `/health/ready` | PostgreSQL connectivity (5-second timeout) and "no pending migrations" for every module context (result cached 30 seconds, failures not cached); Redis when `Cache:Provider=Redis`. | Readiness probe and load balancer membership. |

The PostgreSQL and migrations checks are `Unhealthy` on failure, so `/health/ready` returns 503 until `migrate` has run and while the database is unreachable. The Redis check reports `Degraded`, which still returns 200: a cache outage must not take every replica out of rotation. Blob storage and SMTP have no readiness check; their failures surface in the requests and jobs that use them. This deviates from D-20, which expected Degraded checks for them.

## What to alert on

- `/health/ready` failing on all replicas: the database is down or migrations are pending.
- Any increase of `outbox.dead`.
- A sustained rate of 503 `session_store_unavailable` or `database_unavailable`.
- `SecurityEvent RefreshTokenReuse` events: a stolen or mishandled refresh token.
- The forwarded-headers warning at startup in a deployment behind a proxy: client IPs (and so rate limits) are wrong.
