# Security hardening for production

> Applies to: Acme.Service template

The template is secure by default where that costs nothing: authorization fails closed, tokens are validated statelessly with RS256 only, secrets never live in the repository, the container runs as non-root on a read-only filesystem ([security model](../architecture/security-model.md)). This page covers the steps that depend on your infrastructure and therefore are **not** done for you. Each section says whether it is configuration only or needs a code change.

## Encrypt the Data Protection key ring

**Status:** not implemented (R-07, D-27). Needs a small code change.

ASP.NET Core Data Protection protects the TOTP authenticator keys (purpose `auth.totp`), Identity's email-confirmation and password-reset tokens, and the external-login cookie. Its keys are persisted in `auth.data_protection_keys` so that every replica and both command hosts share one key ring, with the application name `acme-service`. They are stored **unencrypted**: anyone with read access to that table (or a database backup) can decrypt the TOTP secrets.

To encrypt the keys at rest, add a key-encryption step where the key ring is configured. Persistence is set up in `src/Modules/Auth/Acme.Service.Modules.Auth/AuthModule.cs` (`AddDataProtection().PersistKeysToDbContext<AuthDbContext>()` in `AddPersistence`), and the application name in `src/Host/Acme.Service.Host/Security/DataProtectionSetup.cs`, which the web app and the command hosts all call. For example, with a certificate mounted from a Secret:

```csharp
services.AddDataProtection()
    .SetApplicationName(ApplicationName)
    .ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword));
```

Alternatives are a cloud key service through its Data Protection integration package (for example Azure Key Vault's `ProtectKeysWithAzureKeyVault`); add such packages through `Directory.Packages.props` and check their licences.

Things to get right:

- Every process that touches the key ring (web app, `migrate`, `auth:grant-role`) must be able to decrypt it, so configure it in the shared setup, not only in the web app.
- Keys created before the change stay readable but unencrypted; new keys (one is created every 90 days by default) are encrypted. To remove the old plaintext keys, let them expire and then delete expired rows, never active ones.
- When the certificate is renewed, keep the old one available for decryption (`UnprotectKeysWithAnyCertificate`) until every key encrypted with it has expired.
- Never lose the certificate or key: without it the TOTP secrets cannot be decrypted, and every user with MFA must enrol again.

## Per-schema database roles

**Status:** configuration and SQL, no code change.

The service already separates DDL from DML by connection string: `ConnectionStrings:PostgresMigrations` is used only by the migration Job, `ConnectionStrings:Postgres` by the app. In development both use the `postgres` superuser. In production use two roles:

| Role | Used by | Rights |
| --- | --- | --- |
| migrator | `migrate` (Kubernetes Secret `acme-service-migrations-secrets`) | Owns every schema of the service; creates and alters tables; runs the seeder. |
| `app_user` (group, no login) and one or more login roles in it | the app and `auth:grant-role` (Secret `acme-service-secrets`), and the migration Job's `ConnectionStrings__Postgres` | `USAGE` on each schema and `SELECT, INSERT, UPDATE, DELETE` on its tables and sequences; no DDL. |

The app uses **one** connection string for every module, so the application role needs DML on all module schemas plus `hangfire`. Truly separate roles per module would require per-module connection strings, which the code does not support. Within one role, schema ownership is still enforced by the code and the architecture tests (INV-06). Granting the rights to a group role and logging in with a member role lets you rotate the application password without downtime ([secrets](secrets.md#rotation)).

Example, run once by a database administrator. Names and passwords are placeholders; pre-creating the schemas lets you set default privileges before the first migration (EF and the Hangfire installer only create a schema when it does not exist):

```sql
CREATE ROLE migrator LOGIN PASSWORD '<password>';
CREATE ROLE app_user NOLOGIN;
CREATE ROLE app_login_1 LOGIN PASSWORD '<password>' IN ROLE app_user;
GRANT CONNECT ON DATABASE acme_service TO migrator, app_user;

-- schema auth (Auth module)
CREATE SCHEMA IF NOT EXISTS auth AUTHORIZATION migrator;
GRANT USAGE ON SCHEMA auth TO app_user;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA auth
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA auth
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO app_user;

-- schema hangfire (Jobs:Provider=Hangfire)
CREATE SCHEMA IF NOT EXISTS hangfire AUTHORIZATION migrator;
GRANT USAGE ON SCHEMA hangfire TO app_user;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA hangfire
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA hangfire
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO app_user;
```

<!-- <sample:orders> -->

While the Orders sample is part of the service, its schema needs the same block:

```sql
-- schema orders (Orders sample)
CREATE SCHEMA IF NOT EXISTS orders AUTHORIZATION migrator;
GRANT USAGE ON SCHEMA orders TO app_user;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA orders
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA orders
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO app_user;
```

Forgetting it (for example missing `USAGE` on `orders`) makes the readiness migration check fail with `Could not read the migration history.` on every pod, so no pod becomes ready.

<!-- </sample:orders> -->

- Every module schema needs such a block. Add it before deploying the release that introduces the module: without `USAGE` on a module schema the readiness check cannot read that module's migration history and **every** pod stays not ready.
- For a database that already has tables, also run `GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA <schema> TO app_user;` and the same for sequences.
- The default privileges cover only objects created by `migrator`. Always run `migrate` as `migrator` (not as a second migrator role), or objects it creates are not granted to `app_user`.
- PostgreSQL advisory locks (used by `migrate`, the seeder and `Locks:Provider=Postgres`) need no grants.
- Require TLS to the database (`SSL Mode=Require` or stricter in both connection strings).
- The template's own tests run as a superuser; run the release once in a staging environment with these roles before relying on them.

## Forwarded headers

**Status:** configuration only.

Behind an ingress or load balancer, the client's address and scheme arrive in `X-Forwarded-For` and `X-Forwarded-Proto`. The service trusts them only from `Http:ForwardedHeaders:KnownNetworks` (CIDR) and `Http:ForwardedHeaders:KnownProxies` (addresses); `X-Forwarded-Host` is never used. With neither set, only loopback is trusted and a warning is logged at startup outside Development.

Getting this wrong has two effects: per-IP rate limits see the proxy's address (so all clients share one limit), and the client IPs recorded with refresh tokens and in logs are wrong. Set the network your ingress controller or load balancer connects from, in the overlay's `config-patch.yaml`:

```yaml
  Http__ForwardedHeaders__KnownNetworks__0: "10.0.0.0/8"     # your pod or node CIDR, not 0.0.0.0/0
```

Never trust every network: a client could then forge its address and evade the rate limits.

## Cookie-based refresh tokens

**Status:** not implemented; an extension (D-10, OQ-03). The template returns the refresh token in the JSON body because it targets API, mobile and backend-for-frontend clients.

For browser applications, the recommended setup needs no change to the service: a backend-for-frontend (BFF) on the application's own origin keeps the refresh token server-side and gives the browser only an HttpOnly session cookie.

If the browser must talk to the service directly, change the token endpoints to use a cookie:

1. In the endpoints that return tokens (`src/Modules/Auth/Acme.Service.Modules.Auth/Features/Login/LoginEndpoint.cs`, `src/Modules/Auth/Acme.Service.Modules.Auth/Features/LoginMfa/LoginMfaEndpoint.cs`, `src/Modules/Auth/Acme.Service.Modules.Auth/Features/Refresh/RefreshEndpoint.cs`, `src/Modules/Auth/Acme.Service.Modules.Auth/Features/ExternalExchange/ExternalExchangeEndpoint.cs`), write the refresh token to a cookie and omit it from the body. Use `HttpOnly`, `Secure`, `SameSite=Strict`, a `__Host-` or `__Secure-` name prefix, `Path=/api/v1/auth` (with `__Secure-`; `__Host-` requires `Path=/`) and `Expires` equal to `refreshTokenExpiresAt`.
2. In `src/Modules/Auth/Acme.Service.Modules.Auth/Features/Refresh/RefreshEndpoint.cs` and `src/Modules/Auth/Acme.Service.Modules.Auth/Features/Logout/LogoutEndpoint.cs`, read the token from the cookie, and clear the cookie on logout, logout-all and refresh failure.
3. Protect against cross-site request forgery: `SameSite=Strict` plus a required custom request header (or an `Origin` check) on refresh and logout.
4. Enable CORS only for the exact front-end origins, with credentials; the template registers no CORS policy.
5. Keep the use cases unchanged: single-use rotation and reuse detection work the same, and the access token still travels in the `Authorization` header.
6. Add the cookie behaviour to the Auth tests and to the anonymous allowlist review.

Record the decision in a new ADR, since it changes the client contract.

## Other production checklist items

- **TLS** terminates at your ingress; the app listens on plain HTTP 8080 and calls neither `UseHttpsRedirection` nor `UseHsts`. Enable HSTS and redirects at the ingress. External OIDC login requires HTTPS end to end.
- **OpenAPI and Scalar** stay off outside Development (`OpenApi:Enabled=false`); the Hangfire dashboard cannot be enabled outside Development.
- **Network policies** are not included in the manifests. Restrict ingress to the app pods to your ingress controller, and egress to PostgreSQL, Redis, object storage, SMTP, the OIDC provider and the OTLP collector.
- **Image provenance:** pin the base images by digest (Dependabot keeps them current) and scan the image (CI runs Trivy and fails on fixable HIGH or CRITICAL findings).
- **Redis** with a password and TLS (`ssl=true` in `Cache:Redis:ConnectionString`).
- **Rate limits** are per replica: the effective limit is the configured value times the replica count. Lockout (5 failures, 15 minutes) is global. Add an edge rate limiter if you need a global per-IP limit.
- **Token lifetime:** keep `Auth:Jwt:AccessTokenLifetime` short (15 minutes by default); it bounds how long a revoked user's access token works on endpoints without the live-session policy.
- **Alerts** on `SecurityEvent RefreshTokenReuse`, sustained 401 or 429 bursts and any `outbox.dead` increase ([observability](../architecture/observability.md)).
