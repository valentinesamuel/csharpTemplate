# ADR-0012: In-process rate limiting

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Anonymous credential endpoints (login, refresh, MFA login, register, password reset, external code exchange) attract credential stuffing, enumeration and email flooding. A shared limiter in Redis would give exact global limits, but it would make authentication depend on the cache: either requests fail when Redis is down, or the limiter fails open, and Auth must never fail open (INV-13).

## Decision

Use ASP.NET Core's built-in rate limiter, in process, with two policies declared by the Auth module (`src/Modules/Auth/Acme.Service.Modules.Auth/RateLimiting/AuthRateLimitingSetup.cs`):

- `auth-credentials` (login, refresh, login/mfa, reset-password, external exchange): a sliding window per client IP, `Auth:RateLimiting:Credentials` (default 10 per minute);
- `auth-email` (register, resend-confirmation, forgot-password): a sliding window per client IP plus a per-normalised-email limit, `Auth:RateLimiting:Email` (default 5 per minute). The email partition is applied by an endpoint filter that reads the buffered body (at most 4 KB), because limiter partitioners cannot read the body.

Rejections are 429 `too_many_requests` with `Retry-After`. `UseRateLimiter()` runs after forwarded headers and routing and before authentication. Identity lockout (5 failures, 15 minutes) is the global, database-backed backstop.

## Options considered

### Option A: built-in in-process limiter plus Identity lockout (chosen)

- Pros: no external dependency, so it cannot fail open or take Auth down; built into the framework; lockout is global through the database.
- Cons: limits are per replica, so the effective limit is the configured limit times the replica count; counters reset on restart.

### Option B: Redis-backed distributed limiter

- Pros: exact global limits.
- Cons: Auth depends on Redis; a Redis outage forces a choice between failing open and refusing all logins.

### Option C: rate limiting only at the ingress or a WAF

- Pros: no application code.
- Cons: not portable across environments; no per-email partitioning; local development has none.

## Consequences

- Size the limits per replica and document the multiplication for operators; add ingress or WAF limits for global protection where needed.
- Correct client IPs depend on `Http:ForwardedHeaders` being configured behind a proxy; otherwise every client shares the proxy's partition and the limit becomes global. A startup warning flags the missing configuration.
- Tests use a test-only client IP header in the test hosts only.

## Related invariants

- INV-13: no cache dependency in authentication paths.
- INV-12: anonymous endpoints are explicit (the rate limits in this ADR apply to those endpoints, but INV-12 itself does not require them).
- INV-18: rejections are ProblemDetails.
