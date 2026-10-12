# Getting started

> Applies to: Acme.Service template

This guide takes you from a fresh clone to a running service, a first authenticated call and your own service name. It assumes the .NET SDK pinned in `global.json` and Docker.

Each step shows the [Task](task-commands.md) command first (`task` is optional: `brew install go-task`, `winget install Task.Task` or [taskfile.dev/installation](https://taskfile.dev/installation)), then the raw command it runs. `task dev:setup` does steps 1 and 2 (without the build) and starts PostgreSQL in one go.

## 1. Build

```bash
task dev:setup   # dotnet tool restore, locked restore, step 2, PostgreSQL
task b           # dotnet build Acme.Service.slnx -c Release
```

Without `task`:

```bash
dotnet tool restore
dotnet build Acme.Service.slnx -c Release
```

`dotnet tool restore` installs the pinned local tools from `.config/dotnet-tools.json` (`dotnet-ef`). The build treats warnings as errors.

## 2. Generate local secrets

```bash
task dev:env                    # skipped when deploy/compose/.env already exists
dotnet run scripts/dev-env.cs   # without task
```

`scripts/dev-env.cs` generates an RSA-3072 JWT signing key and random PostgreSQL, Redis and S3 passwords, fills every `CHANGE_ME` of `deploy/compose/.env.example` and writes `deploy/compose/.env` (gitignored, mode 0600). It also prints `dotnet user-secrets set ...` commands for running the Host outside Docker. It refuses to overwrite an existing `.env`; pass `-- --force` to regenerate. Never commit the generated file or paste the printed commands into tickets or chat: they contain secrets.

## 3a. Run everything in Docker Compose

```bash
task up   # docker compose -f deploy/compose/compose.yaml up -d --build
curl -s -o /dev/null -w '%{http_code}\n' localhost:8080/health/ready
```

Without `task`:

```bash
docker compose -f deploy/compose/compose.yaml up -d --build
curl -s -o /dev/null -w '%{http_code}\n' localhost:8080/health/ready
```

The stack (`deploy/compose/compose.yaml`) starts PostgreSQL, Redis, RustFS (S3-compatible), Azurite, Mailpit, a one-shot `migrate` service and the app. The app starts only after `migrate` exited 0. It runs with production-grade providers (Redis, S3, SMTP to Mailpit, Hangfire, PostgreSQL locks) and `ASPNETCORE_ENVIRONMENT=Production`, so the OpenAPI document, Scalar and the Hangfire dashboard are not mapped.

| URL | What |
| --- | --- |
| `http://localhost:8080` | The API |
| `http://localhost:8025` | Mailpit: every email the service sends (verification and reset links) |

Port clashes: change `APP_PORT`, `POSTGRES_PORT` and the other `*_PORT` values in `deploy/compose/.env`. To try Azure Blob instead of S3, set `BLOB_PROVIDER=AzureBlob` there. Stop with `task down` or `docker compose -f deploy/compose/compose.yaml down`; `task docker:reset` (asks first) or the same `down` with `-v` also deletes the data volumes. `task docker:ps` and `task docker:logs SVC=app` show the containers and the app log.

## 3b. Or run the Host with `dotnet run`

Useful for debugging. Development uses in-process providers (in-memory cache and jobs, local-disk blobs under `.data/blobs`, emails written to the console log) and PostgreSQL advisory locks, so only PostgreSQL is needed.

```bash
task docker:up:infra   # PostgreSQL only (task dev:setup already started it)
# run the `dotnet user-secrets set ...` commands printed by scripts/dev-env.cs
task db:migrate        # sets DOTNET_ENVIRONMENT=Development for the migrate verb
task r                 # dotnet run --project src/Host/Acme.Service.Host
```

Without `task`:

```bash
docker compose -f deploy/compose/compose.yaml up -d postgres
# run the `dotnet user-secrets set ...` commands printed by scripts/dev-env.cs
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate
dotnet run --project src/Host/Acme.Service.Host
```

- The web host composition never migrates (INV-16); the `migrate` verb must run first (leave `Migrations:RunOnStart` at its default `false` locally) and after every pull that adds migrations. Until then `/health/ready` returns 503.
- `DOTNET_ENVIRONMENT=Development` is needed for the verbs: they build a plain .NET host that ignores the launch profile's `ASPNETCORE_ENVIRONMENT`, and without it they would not read `appsettings.Development.json` or user secrets (`--environment=Development` after the verb works too).
- The launch profile listens on `http://localhost:8080` and sets `ASPNETCORE_ENVIRONMENT=Development`, which enables `/openapi/v1.json` and the Scalar UI at `/scalar`.
- Emails are not sent: the console provider logs the recipient, the subject and, in Development only, the body with the link.
- If PostgreSQL runs on another port, change the `ConnectionStrings:*` user secrets accordingly.

## 4. Make a first authenticated call

Every endpoint needs an access token unless it is explicitly anonymous; even unknown routes return 401.

```bash
curl -s -XPOST localhost:8080/api/v1/auth/register -H 'content-type: application/json' \
  -d '{"email":"admin@example.com","password":"correct horse battery staple","displayName":"Admin"}'
```

The response is always 202. Open the verification email (Mailpit, or the console log with `dotnet run`), take the `userId` and `token` query parameters from the link, URL-decode the token and confirm, then log in. The full sequence, with every other flow, is in the [auth guide](auth.md).

## 5. Bootstrap the first administrator

There is no default admin account. Grant the role from the command line after the user has registered:

```bash
# Compose, with task
task docker:grant-admin EMAIL=admin@example.com

# Compose: reuse the migrate service's image and connection string
docker compose -f deploy/compose/compose.yaml run --rm migrate auth:grant-role --email admin@example.com --role Admin

# Local dotnet run
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- auth:grant-role --email admin@example.com --role Admin
```

The verb is idempotent and exits 1 when the user or the role does not exist. Log in again (or refresh) to get a token with the new permissions.

## 6. Start your own service

Run the init script on a fresh, clean clone:

```bash
task init:dry NAME=Contoso.Billing   # preview
task init NAME=Contoso.Billing       # add -- --remove-sample to drop the Orders sample
```

Without `task`:

```bash
dotnet run scripts/init.cs -- --name Contoso.Billing --dry-run   # preview
dotnet run scripts/init.cs -- --name Contoso.Billing             # add --remove-sample to drop the Orders sample
```

It replaces the three template tokens (`Acme.Service`, `acme-service`, `acme_service`) everywhere, renames files and folders, optionally removes the sample, deletes the build-plan artifacts and prints the next steps: restore with `--force-evaluate` (the lock files name the projects), `dotnet format`, build, test and `dotnet run scripts/dev-env.cs`. It refuses to run on a dirty working tree unless `--force` is given.

<!-- <sample:orders> -->

What `--remove-sample` removes, and how to remove the sample by hand later, is in [removing the sample](removing-the-sample.md).

<!-- </sample:orders> -->

## Next steps

- [Configuration reference](configuration.md)
- [Adding a module](adding-a-module.md) and [adding a use case](adding-a-use-case.md)
- [Testing](testing.md)
- [Command cookbook](../cookbook.md)
- [Task commands](task-commands.md)
- [Deployment](../operations/deployment.md)
