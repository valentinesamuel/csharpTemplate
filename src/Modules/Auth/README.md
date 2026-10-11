# Auth module

> Applies to: Acme.Service template

Identity, tokens, sessions, MFA, role-based access control and the optional external OIDC login for the whole service. Schema `auth`. How to call it: [auth guide](../../../docs/guides/auth.md). Why it is built this way: [security model](../../../docs/architecture/security-model.md).

## Projects

| Project | Visibility | Contents |
| --- | --- | --- |
| `src/Modules/Auth/Acme.Service.Modules.Auth.Contracts/` | Public, for other modules | `AuthPermissions` (`users.read`, `users.manage`, `roles.read`), `IUserDirectory` and `UserSummary` (look up a user's email and display name), `IntegrationEvents/UserRegisteredV1` (`auth.user-registered.v1`). |
| `src/Modules/Auth/Acme.Service.Modules.Auth/` | Only `AuthModule` is public | Everything else, `internal`. |

Other modules reference only the Contracts project (INV-01). Auth itself depends on no other module.

## The documented exception

Unlike other modules, Auth uses EF Core and ASP.NET Core Identity throughout the assembly, not only in its `Persistence` namespace, because Identity's stores, managers and sign-in services are EF-based ([ADR-0005](../../../docs/adr/0005-auth-module-uses-identity-and-ef.md), INV-05). It still must not use Npgsql types directly, and the exception does not extend to any other module; the architecture tests exempt Auth by assembly name only. `MapIdentityApi` is not used: every endpoint is a use case of its own.

## Layout

| Folder | What is in it |
| --- | --- |
| `AuthModule.cs` | The `IModule`: services, persistence (Identity, Data Protection key storage, the seeder, the grant-role command), permissions. |
| `AuthenticationSetup.cs` | JWT bearer as the default scheme, the fallback policy (authenticated user required), the `live-session` policy and its 503 result handler. |
| `Features/` | One folder per use case and endpoint: account (register, confirm, resend, login, login/mfa, refresh, logout, logout-all, me), passwords, MFA, administration, external login. |
| `Tokens/` | `SigningKeyRing` (the key ring), `AccessTokenIssuer`, `JwtOptions`, and the JWKS endpoint `/.well-known/jwks.json`. |
| `Sessions/` | Sessions and refresh tokens (`RefreshTokenService`: single-use rotation with reuse detection), the live-session requirement and handler. |
| `Mfa/` | MFA token issuer (`typ=mfa+jwt`), MFA challenges, `TotpReplayGuard` (TOTP verification with per-user replay protection). |
| `External/` | The OIDC handler setup (only when enabled), options and the one-time exchange codes. |
| `Notifications/` | Internal events for email verification and password reset, their handlers and the jobs that generate the token and send the email. |
| `RateLimiting/` | The `auth-credentials` and `auth-email` policies. |
| `Seeding/` | `PermissionCatalog` (all modules' declared permissions) and `AuthSeeder` (post-migration step). |
| `Persistence/` | `AuthDbContext`, entity configurations, `AuthUserStore` (TOTP key encrypted, recovery codes hashed), migrations. |
| `Directory/` | `UserDirectory`, the implementation of `IUserDirectory`. |
| `Cli/` | `GrantRoleCommand`, behind the `auth:grant-role` verb. |
| `Options/` | Identity, links, default role and MFA options. |

## Data (schema `auth`)

| Table | Holds |
| --- | --- |
| `users`, `roles`, `user_roles`, `user_claims`, `user_logins`, `user_tokens`, `role_claims` | ASP.NET Core Identity, renamed to snake_case plural tables. |
| `permissions`, `role_permissions` | The code-owned permission catalog and the seed roles' grants, rewritten by every `migrate`. |
| `sessions`, `refresh_tokens` | One session per login; refresh tokens as SHA-256 hashes only, each single-use. |
| `mfa_challenges`, `totp_time_steps` | Pending MFA logins (attempt counts) and the last accepted TOTP step per user. |
| `external_login_codes` | One-time exchange codes (hashed, 60 seconds). |
| `data_protection_keys` | The shared Data Protection key ring of all replicas (unencrypted by default). |
| `outbox_messages`, `inbox_messages`, `__ef_migrations_history` | Shared messaging tables and the migration history. |

Migrations live in `src/Modules/Auth/Acme.Service.Modules.Auth/Persistence/Migrations/` and are applied only by `migrate`.

## Configuration

Sections `Auth:Jwt`, `Auth:Identity`, `Auth:RateLimiting`, `Auth:Links`, `Auth:DefaultRole`, `Auth:Mfa` and `Auth:External:Oidc`, all validated at startup. Defaults, environment-variable forms and secrets: [configuration](../../../docs/guides/configuration.md). The signing keys must be supplied per environment (`scripts/dev-env.cs` locally, a mounted Secret in Kubernetes); rotation: [key rotation](../../../docs/operations/key-rotation.md).

## Events and jobs

| Event | Visibility | Flow |
| --- | --- | --- |
| `auth.user-registered.v1` | Public (Contracts) | Raised on registration and on external provisioning; for other modules' snapshots. |
| `auth.email-verification-requested.v1` | Internal | Handler enqueues `SendEmailVerificationJob`, which creates the token and sends the email. |
| `auth.password-reset-requested.v1` | Internal | Handler enqueues `SendPasswordResetJob`. |

Tokens are generated inside the jobs, so they are never stored in the outbox or logged (INV-28).

## Command-line verb

`auth:grant-role --email <email> --role <role>`: grants an existing role to an existing user, idempotently, over `ConnectionStrings:Postgres`. Exit 0 on success, 1 when the user or role is missing, 2 on bad usage. It is the only way to create the first administrator ([auth guide](../../../docs/guides/auth.md#bootstrap-the-first-administrator)).

## Extending

- **A new permission for Auth:** add the constant to `AuthPermissions` and the definition to `PermissionCatalog.AuthPermissionDefinitions` ([permissions](../../../docs/guides/permissions.md)).
- **A new anonymous endpoint:** `.AllowAnonymous()` plus an entry in `tests/Acme.Service.IntegrationTests/Security/AnonymousEndpointAllowlist.cs`, and a rate-limit policy if it accepts credentials or email addresses.
- **A new sensitive endpoint:** add `.RequireAuthorization(LiveSessionRequirement.PolicyName)` so revoked sessions are rejected immediately.
- **More user data for other modules:** extend `IUserDirectory` in Contracts additively, or add fields to a new event version (`UserRegisteredV2`).
- **Cookie-based refresh tokens:** see [security hardening](../../../docs/operations/security-hardening.md#cookie-based-refresh-tokens).

## Tests

`tests/Acme.Service.Modules.Auth.Tests/` (tokens, sessions, account flows, MFA, passwords, admin, external login, seeding, CLI) and the security and resilience suites in `tests/Acme.Service.IntegrationTests/`. See [testing](../../../docs/guides/testing.md).
