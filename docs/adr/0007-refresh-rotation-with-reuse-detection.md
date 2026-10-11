# ADR-0007: Refresh token rotation with reuse detection, strictly single-use

> Applies to: Acme.Service template

- **Status:** Accepted, pending design-review confirmation
- **Date:** 2026-10-08

## Context

Access tokens live 15 minutes; clients keep a long-lived refresh token to get new ones. A stolen refresh token must not give an attacker indefinite access, and theft should be detectable. The template targets API, mobile and backend-for-frontend clients, so refresh tokens travel in the JSON body rather than a browser cookie.

## Decision

Every refresh token is single-use. A login creates a session (`auth.sessions`, the token family) and a first refresh token. `POST /api/v1/auth/refresh` runs in one unit of work (`src/Modules/Auth/Acme.Service.Modules.Auth/Sessions/RefreshTokenService.cs`):

1. find the token by SHA-256 hash; unknown is 401;
2. if it was already used, revoke the session (`reuse_detected`), log `SecurityEvent RefreshTokenReuse` with ids only, and return 401; if the token is expired or the session is revoked or past its absolute expiry, return 401 without revoking anything;
3. if the user is locked, deleted or no longer allowed, revoke and return 401;
4. mark it used with `UPDATE ... SET used_at = now WHERE id = @id AND used_at IS NULL`; if no row changed, treat it as reuse (step 2);
5. insert the next token in the same session, expiring at the earlier of `Auth:Jwt:RefreshTokenLifetime` and the session's absolute expiry;
6. issue an access token with freshly computed permissions.

All client-visible failures are the same 401 `invalid_refresh_token`. Revocations commit even though the request fails.

## Options considered

### Option A: strictly single-use with family revocation (chosen)

- Pros: a replayed token is detected and kills the whole family, limiting a thief to one use; simple to reason about; no timing window to tune.
- Cons: two concurrent refreshes by a legitimate client look like theft and log the user out, so clients must serialise refreshes.

### Option B: grace window for recently rotated tokens

- Pros: tolerates concurrent refreshes from tabs or retries.
- Cons: a stolen token is usable during the window; more state and more edge cases. Can be added later in a new ADR if clients need it.

### Option C: non-rotating refresh tokens

- Pros: simplest.
- Cons: theft is undetectable until expiry.

### Transport alternatives

An HttpOnly SameSite cookie was considered and deferred: it suits browser-only clients, and can be added as an extension for a backend-for-frontend.

## Consequences

- Only token hashes are stored; a database dump does not yield usable refresh tokens.
- Two parallel refreshes with one token: exactly one succeeds, and the session is then revoked (tested in `tests/Acme.Service.Modules.Auth.Tests/Tokens/RefreshTokenTests.cs`).
- Logout revokes the presented token's session; logout-all, password change, password reset and admin lock revoke sessions too. Outstanding access tokens stay valid until they expire, except on `live-session` endpoints.
- Refresh needs the Auth database: during an outage it answers 503 and clients retry.

## Related invariants

- INV-15: refresh tokens hashed and single-use; reuse revokes the family.
- INV-13: the database is consulted by refresh and `live-session` only, never by token validation.
- INV-28: tokens are never logged.
