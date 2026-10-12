# Security model

> Applies to: Acme.Service template

The service authenticates its own users (the Auth module, `src/Modules/Auth/Acme.Service.Modules.Auth/`) and authorizes every request from claims in a short-lived signed access token. The guiding rule is **fail closed**: when something is missing, misconfigured or unreachable, the request or the process is refused, never allowed.

## Fail-closed authorization

- JWT bearer is the default scheme and the only one that authenticates API calls (with OIDC enabled, the external cookie and OIDC schemes are also registered for the login handshake), and the authorization `FallbackPolicy` requires an authenticated user (`src/Modules/Auth/Acme.Service.Modules.Auth/AuthenticationSetup.cs`). Every endpoint, and every unmatched route, needs a valid access token unless it explicitly calls `.AllowAnonymous()` (INV-12).
- Each anonymous endpoint must also be listed, with a justification, in `tests/Acme.Service.IntegrationTests/Security/AnonymousEndpointAllowlist.cs`. `tests/Acme.Service.IntegrationTests/Security/AnonymousEndpointAllowlistTests.cs` enumerates every mapped endpoint, also with OIDC enabled, and fails on any anonymous endpoint that is not listed.
- The anonymous set is: health probes, JWKS, register, confirm-email, resend-confirmation, login, login/mfa, refresh, forgot-password, reset-password, the three external-login routes (only mapped when OIDC is enabled), and the OpenAPI, Scalar and Hangfire dashboard routes (mapped only in Development).

## Access tokens

| Property | Value |
| --- | --- |
| Algorithm | RS256 only (`ValidAlgorithms`); `alg=none`, HS256 and key-confusion tokens are rejected. |
| Type header | `typ=at+jwt` (`ValidTypes`). MFA tokens (`typ=mfa+jwt`), refresh tokens and external login codes can never authenticate an API call (INV-14). |
| Issuer and audience | `Auth:Jwt:Issuer`, `Auth:Jwt:Audience` (default `acme-service`). |
| Lifetime | `Auth:Jwt:AccessTokenLifetime` (15 minutes), `exp` required, checked against the injected `TimeProvider` with `Auth:Jwt:ClockSkew` (30 seconds). |
| Claims | `sub`, `jti`, `sid` (session id), `email`, `role[]`, `perm[]`, `amr[]` (`pwd`, `mfa`, `ext`). |
| Validation | Stateless (INV-13): signature by the key whose `kid` matches in the local key ring, plus the checks above. No authority, no metadata or JWKS fetch, no database or cache lookup. An unknown or missing `kid` resolves to no key, so the token is rejected. |

Permissions are computed from the user's roles when the token is issued and travel in `perm` claims; `RequirePermission("<resource>.<action>")` adds a `perm:<name>` policy that checks the claim ([ADR-0013](../adr/0013-permissions-in-the-access-token.md)). A role change therefore takes effect at the next refresh, at most one access-token lifetime later.

## Signing keys

`Auth:Jwt:SigningKeys` is a key ring (`src/Modules/Auth/Acme.Service.Modules.Auth/Tokens/SigningKeyRing.cs`, [ADR-0006](../adr/0006-rs256-jwt-key-ring-and-rotation.md)):

- each key has a `Kid`, a `Status` of `Active`, `Next` or `Retiring`, and exactly one of `PrivateKeyPath` (a mounted PEM file) or `PrivateKeyBase64`;
- startup fails unless there is exactly one `Active` key, every `Kid` is unique and every key is RSA with at least 2048 bits;
- tokens are signed with the `Active` key and validated with all three statuses;
- `GET /.well-known/jwks.json` publishes the public part of every key with `Cache-Control: public,max-age=300`.

Keys are loaded at startup only. Rotation, done by configuration plus a rolling restart:

1. Add the new key as `Next` on every replica; wait at least the JWKS cache age.
2. Make the new key `Active` and the old one `Retiring` on every replica.
3. Wait at least the access-token lifetime plus clock skew.
4. Remove the old key.

Step 1 must reach all replicas before step 2 starts. Local keys come from `scripts/dev-env.cs`; when deployed the key is `Auth__Jwt__SigningKeys__<n>__PrivateKeyBase64`, set as a Render environment variable ([secrets](../operations/secrets.md)). No key material is committed.

## Sessions and refresh tokens

- Login creates a session (`auth.sessions`) and a refresh token. A refresh token is 32 random bytes sent to the client as base64url; only its SHA-256 hash is stored (`auth.refresh_tokens`, INV-15).
- Refresh tokens are strictly single-use ([ADR-0007](../adr/0007-refresh-rotation-with-reuse-detection.md)). Refresh marks the presented token used with a conditional `UPDATE ... WHERE used_at IS NULL`, issues a new refresh token in the same session and a new access token with fresh permissions. A second use of a token, including two parallel refreshes with the same token, is treated as theft: the whole session is revoked (`reuse_detected`) and a `SecurityEvent` `RefreshTokenReuse` is logged. Clients must serialise their refresh calls.
- Every refresh failure is the same 401 `invalid_refresh_token`.
- Refresh token lifetime is `Auth:Jwt:RefreshTokenLifetime` (14 days), bounded by the session's absolute lifetime `Auth:Jwt:SessionAbsoluteLifetime` (30 days).
- Refresh tokens travel in the JSON body, not in a cookie (the template targets API, mobile and backend-for-frontend clients).

### The live-session policy

Access tokens stay valid until they expire, even after logout. Sensitive endpoints therefore add the `live-session` policy (`src/Modules/Auth/Acme.Service.Modules.Auth/Sessions/LiveSessionHandler.cs`), which checks that the token's `sid` refers to a non-revoked session in `auth.sessions`:

- used by change-password, `mfa/*` setup and management, logout-all and every admin endpoint;
- reads only the Auth database, never Redis or the cache;
- a revoked session gives 401 `session_revoked`;
- if the lookup cannot complete (database down, timeout) the request is denied with 503 `session_store_unavailable` and `Retry-After`, never allowed (INV-13);
- endpoints without the policy keep validating tokens statelessly, so a database outage does not change their authentication.

## Credentials and enumeration resistance

- Passwords: ASP.NET Core Identity hashing, minimum 12 characters, no composition rules, unique email. Lockout after 5 failures for 15 minutes; the lockout counter is in the database, so it is global across replicas.
- Register, resend-confirmation and forgot-password always return 202 with a generic body. Login failures (unknown user, wrong password, locked) all return 401 `invalid_credentials`, and a dummy password hash runs for unknown users to equalise timing. `email_not_verified` (403) is returned only after a correct password.
- Confirmation and reset tokens are generated when the email job runs, never stored in the outbox, and never logged.
- Per-IP and per-email rate limits protect the anonymous credential endpoints ([ADR-0012](../adr/0012-in-process-rate-limiting.md)). Limits are per replica.

## MFA

- TOTP (RFC 6238, 6 digits, 30-second step, plus or minus one step). The authenticator key is encrypted at rest with Data Protection (purpose `auth.totp`); recovery codes are stored as SHA-256 hashes and shown once.
- When MFA is enabled, login returns an `mfaToken` (`typ=mfa+jwt`, audience `<Audience>:mfa`, 5 minutes) instead of tokens. `POST /api/v1/auth/login/mfa` accepts it with a code; each challenge allows fewer than 5 attempts, failures count towards lockout, and `src/Modules/Auth/Acme.Service.Modules.Auth/Mfa/TotpReplayGuard.cs` rejects a time step that was already accepted for the user.

## External OIDC login

Disabled by default (`Auth:External:Oidc:Enabled=false`); when disabled the routes are not mapped. When enabled ([ADR-0014](../adr/0014-external-idp-via-oidc-with-code-exchange.md)):

- authorization code flow with PKCE against a configured authority (HTTPS outside Development);
- `returnUrl` must exactly match an entry of `Auth:External:Oidc:AllowedReturnUrls` (no open redirect);
- a new external identity is provisioned only when no local account has that email; an existing local account is never linked automatically (`returnUrl?error=account_exists`);
- the callback redirects with a one-time code (32 random bytes, stored hashed, valid 60 seconds), which the client exchanges for tokens at `POST /api/v1/auth/external/exchange`. No token ever appears in a URL.

## Data Protection

Data Protection keys are stored in `auth.data_protection_keys` and shared by all replicas, with application name `acme-service` (`src/Host/Acme.Service.Host/Security/DataProtectionSetup.cs`). The key ring is not encrypted at rest by default; encrypting it with a certificate or a key management service is a production hardening step.

## Secrets

- Secrets come only from environment variables, mounted files or a secret store (INV-11). `appsettings*.json` contain none; `deploy/compose/.env.example` contains placeholders only, and the generated `deploy/compose/.env` is gitignored.
- The single allowlisted exception is the public Azurite development account key used by the local compose stack (`.gitleaks.toml`).
- When deployed, secrets are Render environment variables (`sync: false` in `render.yaml`, values entered in the dashboard), not baked into the image or committed; no manifest holding secret values is committed. With `Migrations__RunOnStart=true` the migration runs in the service container, so the DDL connection string is held in the service environment too (the process unsets it after migrating, defence in depth only); the DDL/DML separation is enforced by the PostgreSQL roles ([ADR-0009](../adr/0009-migrations-as-a-separate-job.md)). See [secrets](../operations/secrets.md).
- Logs never contain passwords, tokens, codes, TOTP secrets, connection-string passwords or `Authorization` headers (INV-28). Request logging records method, path without query string, status and duration only.

## Runtime hardening

- The container image runs as a non-root user on a chiseled runtime image (`Dockerfile`).
- The container runs as a non-root user and supports a read-only root filesystem (`--read-only --tmpfs /tmp`); run it with no privilege escalation and all capabilities dropped (INV-22, [deployment](../operations/deployment.md)).
- Forwarded headers are trusted only from `Http:ForwardedHeaders:KnownNetworks` and `KnownProxies`; with neither set, only loopback is trusted and a warning is logged.
- Development-only providers (local blob, console email, in-memory jobs and locks) refuse to start in `Production`.

## Related decisions

- [ADR-0005](../adr/0005-auth-module-uses-identity-and-ef.md), [ADR-0006](../adr/0006-rs256-jwt-key-ring-and-rotation.md), [ADR-0007](../adr/0007-refresh-rotation-with-reuse-detection.md), [ADR-0012](../adr/0012-in-process-rate-limiting.md), [ADR-0013](../adr/0013-permissions-in-the-access-token.md), [ADR-0014](../adr/0014-external-idp-via-oidc-with-code-exchange.md)
