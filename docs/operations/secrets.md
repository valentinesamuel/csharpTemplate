# Secrets

> Applies to: Acme.Service template

Secrets come only from environment variables, mounted files or a secret store, never from the repository, the image, build arguments or logs (INV-11). No `kind: Secret` object is committed; `scripts/ci/validate-k8s.sh` fails if an overlay renders one, and CI runs gitleaks over the whole history with `.gitleaks.toml`.

## What is a secret

| Configuration key (environment variable) | Why |
| --- | --- |
| `ConnectionStrings__Postgres` | Application database password |
| `ConnectionStrings__PostgresMigrations` | Migrator (DDL) database password |
| `Cache__Redis__ConnectionString` | Redis password |
| `Blob__S3__AccessKey`, `Blob__S3__SecretKey` | S3 or S3-compatible credentials (omit both to use the AWS default credential chain, for example IRSA) |
| `Blob__AzureBlob__ConnectionString` | Azure storage account key (or use `Blob__AzureBlob__ServiceUri` with workload identity, which needs no secret) |
| `Notifications__Smtp__Username`, `Notifications__Smtp__Password` | SMTP relay credentials |
| The JWT private keys (`Auth__Jwt__SigningKeys__<n>__PrivateKeyPath` files, or `Auth__Jwt__SigningKeys__<n>__PrivateKeyBase64`) | Anyone holding them can mint access tokens |
| `Auth__External__Oidc__ClientSecret` | OIDC client secret, only when external login is enabled |

Everything else (providers, bucket names, hosts, URLs, lifetimes, the key ids and statuses) is ordinary configuration. The full list is in [configuration](../guides/configuration.md).

## Kubernetes: which Secret holds what

Create the three Secrets per namespace with your secret tooling (External Secrets Operator, Sealed Secrets, Vault, a CSI driver) or, for a first installation, `kubectl`.

| Secret | Keys | Consumed by |
| --- | --- | --- |
| `acme-service-secrets` | `ConnectionStrings__Postgres`, `Cache__Redis__ConnectionString`, `Blob__S3__AccessKey`, `Blob__S3__SecretKey` (or `Blob__AzureBlob__ConnectionString`), `Notifications__Smtp__Username`, `Notifications__Smtp__Password`, optionally `Auth__External__Oidc__ClientSecret` | The app, as environment variables (`envFrom`). The migration Job reads only `ConnectionStrings__Postgres` from it. |
| `acme-service-jwt-keys` | `signing-key-0.pem` (RSA PEM private key; more files during a rotation) | The app, mounted read-only at `/etc/acme-service/jwt` and referenced by `Auth__Jwt__SigningKeys__<n>__PrivateKeyPath` in the ConfigMap. |
| `acme-service-migrations-secrets` | `ConnectionStrings__PostgresMigrations` (a role allowed to run DDL) | The migration Job only; never given to the app. |

Keys that a deployment does not use (for example the S3 keys when using workload identity) can be left out of `acme-service-secrets`; the options validation then decides whether the remaining configuration is complete.

Example with placeholders only; prefer `--from-file` or `--from-env-file` with files outside the repository so values never reach shell history:

```bash
NS=acme-service-prod

kubectl create secret generic acme-service-secrets -n "$NS" --from-env-file=<path-outside-repo>/app-secrets.env
kubectl create secret generic acme-service-jwt-keys -n "$NS" --from-file=signing-key-0.pem=<path-outside-repo>/signing-key-0.pem
kubectl create secret generic acme-service-migrations-secrets -n "$NS" \
  --from-literal=ConnectionStrings__PostgresMigrations='Host=<host>;Port=5432;Database=acme_service;Username=<migrator>;Password=<password>;SSL Mode=Require'
```

`deploy/k8s/README.md` shows the same commands with `--from-literal` for every key.

A missing Secret or key keeps the pod in `CreateContainerConfigError`; incomplete configuration stops startup with the key named. Neither fails silently.

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
| Redis, S3, SMTP, OIDC client secret | Update the Secret, roll the deployment, revoke the old credential. Configuration is read at startup, so pods only pick up a new value when they restart. |

### Database passwords

A PostgreSQL role has one password, so changing it in place breaks every pod that still uses the old one. Rotate per role:

- **Application role** (`ConnectionStrings__Postgres` in `acme-service-secrets`, used by the app pods **and** read by the migration Job, see `deploy/k8s/migration/base/job.yaml`): create a second login role with the same rights, switch to it, then drop the old login.

  ```sql
  CREATE ROLE app_login_2 LOGIN PASSWORD '<new password>' IN ROLE app_user;
  ```

  Update `ConnectionStrings__Postgres` in `acme-service-secrets` to use `app_login_2`, run `kubectl rollout restart deployment/acme-service -n <ns>` and wait for `kubectl rollout status`. When no pod and no running migration Job uses the old login any more, `DROP ROLE app_login_1;`. This works when the rights are granted to a group role (`app_user`) that the login roles are members of ([database roles](security-hardening.md#per-schema-database-roles)); if your rights are granted directly to the login role, grant them to the new role first.
- **Migrator role** (`ConnectionStrings__PostgresMigrations` in `acme-service-migrations-secrets`, used only by the migration Job): change its password between deployments, when no Job is running (`ALTER ROLE migrator PASSWORD '<new password>';`), then update the Secret before the next Job. Do not switch to a second migrator role: the default privileges are declared `FOR ROLE migrator`, so objects created by another role would not be granted to the application.

## Things that are not secrets but are protected

- **Data Protection keys** (which encrypt TOTP secrets and Identity tokens) are stored in `auth.data_protection_keys` and shared by all replicas. They are not encrypted at rest by default; see [security hardening](security-hardening.md#encrypt-the-data-protection-key-ring).
- **Refresh tokens, recovery codes and external login codes** are stored only as SHA-256 hashes.
- **Logs** never contain passwords, tokens, codes, TOTP secrets, connection-string passwords or `Authorization` headers (INV-28).
