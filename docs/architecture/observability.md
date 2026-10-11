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

Every console log line is one JSON object followed by a line feed, in **every environment** (Development, Testing, Production) and from **every entry point**: the web host, the startup (bootstrap) logger, the `migrate` and `auth:grant-role` verbs, Hangfire and in-memory jobs and outbox dispatch. All of them use one definition, `LoggingSetup` (`src/Host/Acme.Service.Host/Observability/LoggingSetup.cs`), which adds `Enrich.FromLogContext`, the destructuring limits and a stdout console sink with `JsonLogFormatter` (`src/Host/Acme.Service.Host/Observability/JsonLogFormatter.cs`). There is no text output template anywhere; use `jq` to read logs locally (see [Reading logs](#reading-logs-with-jq)).

### Line contract

- One event is exactly one line: a single, non-indented JSON object and one `\n`. Line-breaking characters inside values (`\r`, `\n`, U+0085, U+2028, U+2029) are escaped, so a multi-line message or stack trace never splits a line.
- Exactly these 14 top-level keys, always in this order, always present (the value may be `null`), and no others:
  `timestamp, level, message, requestId, correlationId, traceId, spanId, service, environment, logger, eventId, meta, requestMeta, exception`.
- The formatter never throws and never writes a partial line. If an event cannot be formatted, it writes a fallback line with the same 14 keys, `message` `"Log event could not be formatted"` and `meta` `{"formatterError": "<exception type name>", "messageTemplate": "<template text>"}`, and reports the failure through Serilog `SelfLog`. If even that fails, a constant line is written (all 14 keys; `timestamp` is `null` on that last-resort line only).
- Non-ASCII text and `<`, `>`, `&`, `'` are written unescaped for readability; lone UTF-16 surrogates are replaced by U+FFFD.

Example, the request-summary line of `POST /orders` (wrapped here for reading; on stdout it is one line):

```json
{"timestamp":"2026-10-11T09:14:03.512Z","level":"Information","message":"HTTP POST /orders responded 201 in 3.1234 ms",
 "requestId":"0HN7K2QF3M1AB00000001","correlationId":"test1","traceId":"4bf92f3577b34da6a3ce929d0e0e4736",
 "spanId":"00f067aa0ba902b7","service":"acme-service","environment":"Production",
 "logger":"Serilog.AspNetCore.RequestLoggingMiddleware","eventId":null,"meta":{},
 "requestMeta":{"method":"POST","path":"/orders","statusCode":201,"durationInMs":3.1234,"isSuccess":true},"exception":null}
```

A line logged by a handler in the same request, `logger.LogInformation("Order {OrderId} placed for {TotalAmount}", "o-42", 19.99m)`, has `"message":"Order o-42 placed for 19.99"`, `"meta":{"orderId":"o-42","totalAmount":19.99}` and `"requestMeta":{"method":"POST","path":"/orders","statusCode":null,"durationInMs":null,"isSuccess":null}`, with the same `requestId`, `correlationId` and `traceId`.

### Fields

| Key | Source | Type / format | `null` when |
| --- | --- | --- | --- |
| `timestamp` | Event time, converted to UTC | `yyyy-MM-ddTHH:mm:ss.fffZ` (milliseconds, `Z`) | never (except the constant last-resort line) |
| `level` | Serilog level name | `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. Microsoft.Extensions.Logging `Critical` arrives as `Fatal`, `Trace` as `Verbose`. | never |
| `message` | The message template rendered with the invariant culture. String values are written literally (no surrounding quotes, like Serilog `{Message:l}`); format specifiers are honoured (`{Elapsed:0.0000}`); destructured values use Serilog's text form. | string | never (empty string for an empty template) |
| `requestId` | Property `RequestId` (`HttpContext.TraceIdentifier`) | string | outside HTTP |
| `correlationId` | Property `CorrelationId` (see [Correlation sources](#correlation-sources)) | string | no correlation id in scope |
| `traceId` | Current W3C trace id | 32 lowercase hex | no current activity |
| `spanId` | Current W3C span id | 16 lowercase hex | no current activity |
| `service` | `Observability:ServiceName`; the bootstrap logger reads the `Observability__ServiceName` environment variable, else `acme-service`. Same value as the OTLP `service.name`. | string | blank setting |
| `environment` | Host environment name; the bootstrap logger reads `ASPNETCORE_ENVIRONMENT`, then `DOTNET_ENVIRONMENT`, else `Production` | string | unknown |
| `logger` | Property `SourceContext` (the logger category) | string | static `Log.*` calls without a category (bootstrap logger, verb usage errors) |
| `eventId` | Property `EventId`: its numeric `Id` (the event name is dropped) | integer | no event id, or not an integer |
| `meta` | Call-site properties, see [meta](#meta-call-site-data) | object | see below |
| `requestMeta` | HTTP context, see [requestMeta](#requestmeta-http-context) | object | outside HTTP |
| `exception` | `Exception.ToString()`: type, message, stack trace, inner exceptions | string | no exception |

`service` and `environment` are fixed per logger and are not event properties, so no call site can override them.

### `meta`: call-site data

`meta` holds the properties the caller supplied: message template holes, `BeginScope` dictionaries and `ForContext` properties, minus the reserved names below.

Reserved names live in one place, `Acme.Service.BuildingBlocks.Logging.LogPropertyNames`:

- **Always reserved** (never in `meta`; they feed the top-level keys or are infrastructure noise): `CorrelationId`, `RequestId`, `SourceContext`, `EventId`, `TraceId`, `SpanId`, `ParentId`, `RequestPath`, `ConnectionId`, `ActionId`, `ActionName`, `Scope`, `{OriginalFormat}`, `Http.Method`, `Http.Path`, `Http.StatusCode`, `Http.DurationMs`, `Http.IsSuccess`.
- **Reserved on the request-summary event only** (the event that carries `Http.StatusCode`): `RequestMethod`, `StatusCode`, `Elapsed`. On every other event they are ordinary call-site properties and appear in `meta` (`requestMethod`, `statusCode`, `elapsed`).
- **Not reserved** (legitimate names used by existing call sites; pinned by tests): `Method`, `Path`, `RoutePattern`, `UseCase`, `ElapsedMs`, `EventType`, `MessageId`, `JobName`, `JobId`, `HangfireMessage`. They always appear in `meta`.

The HTTP context names are dotted (`Http.*`) on purpose: a message template hole cannot contain `.`, so no call site can collide with them by logging. A collision is possible only through an explicit `BeginScope` or `ForContext`.

Rules:

1. Every property that is not reserved goes to `meta`. Positional holes (`{0}`, all-digit names) are skipped; their value is still in `message`.
2. Keys are camelCased (`OrderId` becomes `orderId`; nested structure members too). If the camelCased key is already taken in the same object, the original name is used; if that is taken as well, the property is dropped. Which of two colliding properties wins depends on property order, so avoid names that differ only in case.
3. Values: `null`, strings, booleans and numbers (integers, `decimal`, finite `float`/`double`) map to the JSON equivalent; `NaN`, `Infinity` and `-Infinity` become strings; `DateTime`/`DateTimeOffset` become ISO 8601 round-trip strings, `TimeSpan` the invariant `c` string, `Guid` the `D` string, enums their name. Any other scalar (for example `PathString`, `Uri`, `IPAddress`, `Version`, a custom type) is written as its invariant `ToString()` string, never as an object. Destructured objects become JSON objects (no `$type`), sequences become arrays, dictionaries become objects keyed by the invariant string of the key.
4. Nesting deeper than 10 levels is written as the string `"..."`, whatever the destructuring configuration.
5. Empty `meta` is `{}` on an HTTP line (`requestMeta` is not `null`) and `null` on a non-HTTP line.

### `requestMeta`: HTTP context

- Present (not `null`) if and only if the event carries `Http.Method` or `Http.StatusCode`, i.e. it was logged inside a request (after `CorrelationIdMiddleware`) or it is the request-summary event. Otherwise `null`.
- When present it always has exactly these five keys, in this order; a missing value is `null`:

| Key | Source | Non-null on |
| --- | --- | --- |
| `method` | `Http.Method` (request method) | every line inside a request and the summary |
| `path` | `Http.Path`: `PathBase + Path`, decoded. Never the query string, never a header (INV-28). | every line inside a request and the summary |
| `statusCode` | `Http.StatusCode` (integer) | summary only |
| `durationInMs` | `Http.DurationMs` (number, Serilog's own measurement) | summary only |
| `isSuccess` | `Http.IsSuccess`: `true` when the status is below 400 and no exception was observed (neither thrown out of the pipeline nor handled by the exception handler) | summary only |

- `isSuccess` is forced to `false` whenever the summary event carries an exception, regardless of the status code.
- Handler exceptions are caught by the exception handler inside request logging, so the summary of a failed request has `statusCode` 500, `isSuccess` `false` and `level` `Error`; the exception text itself is on the separate `Error` line written by `Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware`, which carries the same `correlationId`, `requestId` and `requestMeta.path`.
- Health probe summaries (`/health*`) are logged at `Debug`; 5xx and exceptions at `Error`; everything else at `Information`.

### `null` rules at a glance

| Context | `requestId` | `correlationId` | `requestMeta` | empty `meta` |
| --- | --- | --- | --- | --- |
| Inside an HTTP request (after `CorrelationIdMiddleware`) and the request summary | set | set | object | `{}` |
| HTTP lines logged before `CorrelationIdMiddleware` (forwarded-headers middleware) | set (ASP.NET Core hosting scope) | `null` | `null` | `null` |
| Job run, outbox dispatch | `null` | set | `null` | `null` |
| `migrate`, `auth:grant-role` (inside the verb) | `null` | set | `null` | `null` |
| Host startup and shutdown (`Now listening on`, `Application started`) | `null` | `null` | `null` | `null` |
| Startup failure (`Invalid configuration`, `Host terminated unexpectedly`) | `null` | `null` | `null` | `null` |

The `Fatal` lines in the last row are written by `Program` after the verb or web host has returned or thrown, outside any correlation scope, so they have `correlationId` `null` even when a verb failed.

### Correlation sources

- **HTTP.** `CorrelationIdMiddleware` accepts a valid inbound `X-Correlation-Id` (see [Correlation ids](#correlation-ids)) or generates one, and opens a single log scope with `CorrelationId`, `RequestId`, `Http.Method` and `Http.Path`, so every line logged inside the request gets `correlationId`, `requestId` and `requestMeta.method`/`path`. The request-summary event is written by Serilog's request-logging middleware, which does not see that scope, so its `CorrelationId` and `RequestId` are set through the diagnostic context and its `Http.*` values (including status, duration and outcome) are computed explicitly with the same path rule.
- **Jobs (Hangfire and in-memory).** The correlation id that was current at enqueue time is restored for the run. When there is none (recurring jobs, jobs enqueued outside a request), a new id is generated for the run (32 lowercase hex). In-memory retries keep the id of the first attempt. Hangfire retries of a job enqueued without an id get a new id per attempt (the persisted job signature cannot carry it); the attempts share the job name.
- **Outbox dispatch.** The correlation id stored with the message is restored; when the message has none, an id is generated for that dispatch only and is not written back.
- **CLI verbs.** `migrate` and `auth:grant-role` generate one id per invocation (no inbound id is accepted) and push it onto the Serilog log context around the verb, so every line of one run shares it and different runs differ.
- Generated ids are the current trace id when an activity exists, otherwise a random trace id: 32 lowercase hex. Inbound and enqueued ids are restored unchanged.

### Destructuring limits

Set in `LoggingSetup` after `ReadFrom.Configuration`, so code wins over configuration: maximum depth 5, maximum string length 8192 characters, maximum collection count 100. The string limit also applies to values rendered into `message`. The formatter's own depth guard (10 levels, `"..."`) applies on top.

### Writing log calls (caller guide)

- Pass data as named template holes: `logger.LogInformation("Order {OrderId} placed", orderId)`. The value appears in `message` and in `meta.orderId`. Never use string interpolation; the value would be in `message` only and the template would differ per call.
- `{@Order}` destructures an object into nested JSON in `meta.order` (subject to the limits above); without `@` a non-primitive value is written as its `ToString()`.
- Never pass secrets, tokens, codes, passwords, connection strings or email bodies (outside Development) to a log call, in a hole, a scope or an exception message (INV-28). A secret that shows up in `meta` is fixed at the call site; it is never hidden by adding its key to the reserved list.
- Do not use names from the reserved list for call-site data (`RequestPath`, `Scope`, `CorrelationId`, ...): they are dropped from `meta`. `Method`, `Path`, `StatusCode` and friends are fine.
- Do not put secrets in route segments: `requestMeta.path` and the summary `message` contain the full decoded path. Tokens belong in query strings or bodies, which are never logged.
- The use-case pipeline logs the use-case name, outcome, error code and duration (`LoggingBehavior`, event ids 1000 to 1002). It never logs inputs.
- Security-relevant actions are logged as message templates starting with `SecurityEvent {EventType}` (for example locks, logout-all, refresh token reuse, external code exchange) with ids only.

### Operator rules

- **Do not add Serilog sinks through configuration or environment variables** (`Serilog:WriteTo`, `Serilog:Using`, `Serilog__WriteTo__...`). `ReadFrom.Configuration` would honour them and add a second, non-JSON console output, breaking the one-JSON-object-per-line contract. Console logging is defined only in `LoggingSetup`; a test fails if `appsettings*.json` gain a `Serilog:WriteTo` or `Serilog:Using` section, but environment variables are not checked. Use `Serilog:MinimumLevel:*` for levels only.
- **Keep `Serilog:MinimumLevel:Override:Microsoft.AspNetCore` at `Warning` or higher.** At `Information` or lower, ASP.NET Core's own `Microsoft.AspNetCore.Hosting.Diagnostics` "Request starting" event is emitted, and it includes the full URL with the query string in `message` and the query string in `meta.queryString` (for example `"queryString":"?token=..."`). Query strings can carry tokens, so lowering that override breaks the no-query-string guarantee (INV-28). `Microsoft.AspNetCore.Diagnostics` is set explicitly to `Warning` so the exception handler's `Error` lines are kept.
- `migrate --list` (and the `no module contexts registered` notice) prints human-readable command output on stdout. It is the command's result, not a log line, and is not JSON; the log lines of the same run are. Filter with `jq -R 'fromjson?'` when piping that output.
- With OTLP enabled, the same events are exported as OTLP logs; `service.name` equals the JSON `service` field, and the `RequestId` and `Http.*` properties become log attributes.

### Reading logs with `jq`

The service's stdout contains only JSON lines, but what you pipe into `jq` may not: `dotnet run` prints `Using launch settings from ...` and `Building...` first, and `docker compose logs` merges stdout with stderr, where the container image prints a harmless native-library probe (`Cannot load library libgssapi_krb5.so.2`, see the [cookbook](../cookbook.md)). `jq -R 'fromjson? | ...'` skips any non-JSON line.

```bash
# Local run, compact view
dotnet run --project src/Host/Acme.Service.Host | jq -cR 'fromjson? | {timestamp, level, message}'

# Only warnings and errors, with the exception
dotnet run --project src/Host/Acme.Service.Host | jq -R 'fromjson? | select(.level == "Warning" or .level == "Error" or .level == "Fatal")'

# Everything for one request
docker compose -f deploy/compose/compose.yaml logs --no-log-prefix app | jq -cR 'fromjson? | select(.correlationId == "test1")'

# Request summaries: method, path, status, duration
docker compose -f deploy/compose/compose.yaml logs --no-log-prefix app | jq -cR 'fromjson? | select(.requestMeta.statusCode != null) | .requestMeta'

# Failed requests
docker compose -f deploy/compose/compose.yaml logs --no-log-prefix app | jq -cR 'fromjson? | select(.requestMeta.isSuccess == false) | {correlationId, message}'

# stdout only (strict: every line must parse)
docker logs acme-service-app-1 2>/dev/null | jq -c '{level, correlationId, requestMeta}'
```

Use `--no-log-prefix` with `docker compose logs`; without it every line starts with `app-1  | ` and is not JSON.

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

`X-Correlation-Id` is accepted when it is a single value of 1 to 64 characters from `[A-Za-z0-9]` (hyphens and underscores are rejected); otherwise a new id is generated (an invalid inbound value is never echoed). The id is returned in the response header, added to the log scope (the JSON `correlationId` field) and to every problem document and success envelope, stored with outbox messages and job metadata, and forwarded on outbound `HttpClient` calls. Jobs, outbox dispatches and CLI verbs without an id generate one; see [Correlation sources](#correlation-sources).

### Request id

`requestId` is `HttpContext.TraceIdentifier` with every non-alphanumeric character removed (`0HNP7R1GJ7MSQ:00000002` becomes `0HNP7R1GJ7MSQ00000002`), set once by `CorrelationIdMiddleware` ([ADR-0017](../adr/0017-response-envelope.md)). The log field, the `X-Request-Id` response header and the `requestId` member of every success envelope and problem document carry that same value.

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
