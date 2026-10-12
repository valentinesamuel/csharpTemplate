# Secrets

> Applies to: Acme.Service template

Secrets come only from environment variables or mounted files, never from the repository, the image, build arguments or logs (INV-11). On Render they are environment variables set in the dashboard, and CI runs gitleaks over the whole history with `.gitleaks.toml`.

## What is a secret

| Configuration key (environment variable) | Why |
| --- | --- |
| `ConnectionStrings__Postgres` | Application database password |
| `ConnectionStrings__PostgresMigrations` | Migrator (DDL) database password |
| `Cache__Redis__ConnectionString` | Redis password |
| `Blob__S3__AccessKey`, `Blob__S3__SecretKey` | S3 or S3-compatible credentials (omit both to use the AWS default credential chain, for example an instance role) |
| `Blob__AzureBlob__ConnectionString` | Azure storage account key (or use `Blob__AzureBlob__ServiceUri` with workload identity, which needs no secret) |
| `Notifications__Smtp__Username`, `Notifications__Smtp__Password` | SMTP relay credentials |
| The JWT private keys (`Auth__Jwt__SigningKeys__<n>__PrivateKeyPath` files, or `Auth__Jwt__SigningKeys__<n>__PrivateKeyBase64`) | Anyone holding them can mint access tokens |
| `Auth__External__Oidc__ClientSecret` | OIDC client secret, only when external login is enabled |

Everything else (providers, bucket names, hosts, URLs, lifetimes, the key ids and statuses) is ordinary configuration. The full list is in [configuration](../guides/configuration.md).

## Render environment variables

Deployed secrets are plain environment variables on the Render service. `render.yaml` declares each secret as a `sync: false` entry: the Blueprint names the key but holds no value, and Render asks for the value once in the dashboard when the Blueprint is first applied. To share values across several services, put them in a Render environment group instead.

- There is no client id, client secret or login step, and nothing goes in GitHub (no repository secret, no workflow variable). The image's only entrypoint is `dotnet Acme.Service.Host.dll`, which reads the environment as given.
- Configuration is read at startup, so **changing a value requires a redeploy or restart** of the service. Render redeploys when you save an environment variable change; confirm that in the dashboard.
- With `Migrations__RunOnStart=true` the migration runs inside the service container, so the service environment holds `ConnectionStrings__PostgresMigrations` too ([ADR-0009](../adr/0009-migrations-as-a-separate-job.md), [security hardening](security-hardening.md#per-schema-database-roles)).
- The key ids and statuses (`Auth__Jwt__SigningKeys__<n>__Kid`, `__Status`) are not secret. Keys that a deployment does not use (for example the S3 keys when using a workload identity) can be left out; the options validation then decides whether the remaining configuration is complete. The code still supports `PrivateKeyPath` for platforms that mount key files.
- A missing required key stops startup with the key named by the options validation; the new instance never becomes ready and Render keeps the previous one serving.

Be honest about what this is: the dashboard environment store is not a versioned or audited secrets manager. There is no per-value history, no read-audit trail and no fine-grained access policy beyond who can open the service in your Render workspace. Keep a record of rotations yourself, restrict workspace membership, and move to a dedicated secrets manager if you need those properties.

## Docker Compose

`deploy/compose/.env` holds every local credential: PostgreSQL, Redis and S3 (RustFS) passwords and one JWT signing key in the `PrivateKeyBase64` form. `dotnet run scripts/dev-env.cs` generates it from `deploy/compose/.env.example`, which contains `CHANGE_ME` placeholders only. The generated file is gitignored, excluded from the Docker build context (`.dockerignore`) and written with mode 0600.

The single allowlisted exception to "no secrets in the repository" is the public, well-known Azurite development account key in `deploy/compose/compose.yaml`. It only works against the local emulator.

## Local `dotnet run`

Use user secrets (stored in your user profile, outside the repository). `scripts/dev-env.cs` prints the exact commands for the signing key and the two connection strings. Run them in a private terminal.

## Rotation

| Secret | How |
| --- | --- |
| JWT signing key | The four-step runbook in [key rotation](key-rotation.md); a rotation never logs users out. |
| Database passwords | See [database passwords](#database-passwords) below. |
| Redis, S3, SMTP, OIDC client secret | Update the value in the Render dashboard, redeploy the service, revoke the old credential. Configuration is read at startup, so instances only pick up a new value when they restart. |

### Database passwords

A PostgreSQL role has one password, so changing it in place breaks every instance that still uses the old one. Rotate per role:

- **Application role** (`ConnectionStrings__Postgres`, used by the app and by the migration's seeder): create a second login role with the same rights, switch to it, then drop the old login.

  ```sql
  CREATE ROLE app_login_2 LOGIN PASSWORD '<new password>' IN ROLE app_user;
  ```

  Update `ConnectionStrings__Postgres` in the Render dashboard to use `app_login_2`, redeploy and wait until all instances are healthy. When no instance and no running migration uses the old login any more, `DROP ROLE app_login_1;`. This works when the rights are granted to a group role (`app_user`) that the login roles are members of ([database roles](security-hardening.md#per-schema-database-roles)); if your rights are granted directly to the login role, grant them to the new role first.
- **Migrator role** (`ConnectionStrings__PostgresMigrations`, used only by the start-up migration; the process unsets it after migrating): change its password between deployments, when no migration is running (`ALTER ROLE migrator PASSWORD '<new password>';`), then update the value in the Render dashboard before the next deploy. Do not switch to a second migrator role: the default privileges are declared `FOR ROLE migrator`, so objects created by another role would not be granted to the application.

## Things that are not secrets but are protected

- **Data Protection keys** (which encrypt TOTP secrets and Identity tokens) are stored in `auth.data_protection_keys` and shared by all replicas. They are not encrypted at rest by default; see [security hardening](security-hardening.md#encrypt-the-data-protection-key-ring).
- **Refresh tokens, recovery codes and external login codes** are stored only as SHA-256 hashes.
- **Logs** never contain passwords, tokens, codes, TOTP secrets, connection-string passwords or `Authorization` headers (INV-28).
