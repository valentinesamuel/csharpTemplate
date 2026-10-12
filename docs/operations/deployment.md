# Deployment

> Applies to: Acme.Service template

The service ships as one container image (`Dockerfile`) that runs either the web app or, with the `migrate` argument, the schema migration. The supported target is Render (`render.yaml`); the image also runs on any Docker host. Docker Compose in `deploy/compose/compose.yaml` is for local work only.

## Deploy on Render

`render.yaml` is a Render Blueprint: one web service built from the root `Dockerfile`.

1. **Connect the repository.** In the Render dashboard create a Blueprint from this GitHub repository and apply `render.yaml`.
2. **Fill the secrets once.** Every `sync: false` entry (database strings, the JWT key, blob and SMTP credentials, the email link URLs) is requested when the Blueprint is first applied. Nothing is stored in GitHub ([secrets](secrets.md#render-environment-variables)). The names are the configuration keys with `__` for `:` ([configuration](../guides/configuration.md)).
3. **Push.** `autoDeployTrigger: checksPass` deploys a push to the branch only after its GitHub checks pass. Render builds the image itself; no registry is involved.
4. **Migration runs first, in the container.** The Blueprint sets `Migrations__RunOnStart=true`, so each new container runs the `migrate` step (`dotnet Acme.Service.Host.dll migrate`'s code path) before it starts the web app. There is no Render pre-deploy command, so this works on the free plan. `ConnectionStrings__PostgresMigrations` is required. The web host composition still never migrates (INV-16); if the migration fails the container exits 1 before any port opens, the deploy fails and the previous version keeps serving.
5. **Health check.** `healthCheckPath: /health/ready` returns 503 until every module is migrated, so a failed or missing migration never takes traffic.

Settings that matter on Render:

- `Http__ForwardedHeaders__TrustAllProxies=true` is set in the Blueprint. Render's edge is the only way to reach the service and its proxy addresses are dynamic, so listing `KnownNetworks` is not practical. It trusts `X-Forwarded-For` and `X-Forwarded-Proto` from any sender (nearest hop only) and overrides `KnownNetworks` and `KnownProxies`; never set it where the service can be reached directly.
- `Migrations__RunOnStart=true` is set in the Blueprint; without it nothing migrates on Render. A long migration delays readiness because the port is not open yet; run those separately (the `migrate` verb as a one-off job) before deploying.
- `ConnectionStrings__Postgres` and `ConnectionStrings__PostgresMigrations` must be Npgsql key-value strings (`Host=...;Port=5432;Database=...;Username=...;Password=...;SSL Mode=Require`), **not** the `postgres://` URL Render shows for its own databases.
- The image listens on 8080 (`PORT=8080`).
- `Cache__Provider=InMemory` is per instance; set Redis before scaling out.

## What you need first

1. **Configuration and secrets** as environment variables of the deployment: on Render, the `render.yaml` entries; elsewhere, your platform's equivalent. Providers, S3 bucket and region (or Azure container), SMTP host and sender, the email link URLs (`Auth__Links__ConfirmEmailUrl`, `Auth__Links__ResetPasswordUrl`) and, if wanted, `Observability__Otlp__Endpoint`. Behind a proxy you control, set `Http__ForwardedHeaders__KnownNetworks__0` for its network. Replace every placeholder (`*.example.com`, bucket names) ([configuration](../guides/configuration.md)).
2. **PostgreSQL** reachable from the service, with the database created, a migrator role and an application role ([security hardening](security-hardening.md#per-schema-database-roles)). Redis is needed when `Cache__Provider=Redis`.

## Running the image on another Docker host

The image `ENTRYPOINT` is `dotnet Acme.Service.Host.dll`, which reads its configuration from the environment as given. CI builds the image on every pull request and pushes it to GHCR from the `publish` job on a push to `main` and on `v*` tags (`ghcr.io/<owner>/acme-service`, owner lowercased; tags `sha-<short>` and `main`, plus `1.2.3`, `1.2`, `1`, `latest` on a version tag). Deploy a `sha-<short>` or version tag, not `main` or `latest`, so a rollback names an exact image. The first push creates a private package: link it to the repository and grant the host pull access.

Pass every setting as a plain environment variable (`-e`, an uncommitted env file or your platform's secret mechanism). Recommended runtime settings: the image's non-root user (1654), a read-only root filesystem with a writable `/tmp` (`--read-only --tmpfs /tmp`) and a restart policy.

Run the migration first as a one-shot container with `ConnectionStrings__PostgresMigrations` and `ConnectionStrings__Postgres` set, and start the new app only when it succeeded:

```bash
docker run --rm --read-only --tmpfs /tmp \
  --env-file <migration env file> \
  -e Jobs__Provider=Hangfire -e DOTNET_ENVIRONMENT=Production \
  ghcr.io/<owner>/acme-service:<tag> migrate \
  || { echo "migration failed" >&2; exit 1; }
```

## Deploy with task

The root `Taskfile.yml` wraps the steps above for a single Docker host ([task commands](../guides/task-commands.md) covers installing `task`). Render does not need any of this: it deploys on push, and `task deploy:render` only prints the Render checklist from [Deploy on Render](#deploy-on-render). The raw `docker run` commands above remain the reference.

| Task | What it does |
| --- | --- |
| `task deploy:image TAG=<tag>` | `docker build -f Dockerfile -t acme-service:<tag> .`, then fails unless the image user is non-root. |
| `task deploy:migrate TAG=<tag> ENV_FILE=<file> [MIGRATE_ENV_FILE=<file>] [NETWORK=<net>]` | The one-shot migration container (`migrate`, `Jobs__Provider=Hangfire`, `DOTNET_ENVIRONMENT=Production`). A non-zero exit fails the task. |
| `task deploy:run TAG=<tag> ENV_FILE=<file> [NAME=acme-service] [PORT=8080] [BIND=127.0.0.1] [NETWORK=<net>]` | Starts the app container with `--restart unless-stopped`, published on `BIND:PORT`. |
| `task deploy:check [NAME=acme-service] [PORT=8080] [BIND=127.0.0.1] [URL=<url>]` | Fails at once if the container is not running, then polls `/health/ready` from the host until it returns 200 (2 to 3.5 minutes at most; startup can take up to 2 minutes). On failure it prints `docker logs --tail 50 <NAME>`. |
| `task deploy:release TAG=<tag> ENV_FILE=<file> [MIGRATE_ENV_FILE=<file>] [...]` | `deploy:image`, `deploy:migrate`, `deploy:run`, `deploy:check`, strictly in that order. It checks every input first, and a failed migration ends the run before the running app is touched. |

Rules the tasks enforce:

- **Secrets only through env files.** `ENV_FILE` and `MIGRATE_ENV_FILE` must exist and must not be tracked by git; keep them outside the repository with mode 600. Secret values never appear on a command line. Use a separate `MIGRATE_ENV_FILE` holding `ConnectionStrings__PostgresMigrations` and `ConnectionStrings__Postgres`, so that the app's `ENV_FILE` does not hold the DDL connection string; when it is omitted, the migration uses `ENV_FILE` and prints a note.
- **Exact tags.** `TAG` (or the tag of `IMAGE`) must be a `sha-<short>` or version tag; empty, `latest`, `main` and untagged images are rejected. Do not use `TAG=local`: it overwrites the Compose image `acme-service:local`.
- **Local images only.** `deploy:migrate` and `deploy:run` use `--pull never`. `deploy:release` builds the image on the host itself. To deploy the CI image from GHCR, pull it first and pass the full name: `docker pull ghcr.io/<owner>/acme-service:<tag>`, then `task deploy:migrate IMAGE=ghcr.io/<owner>/acme-service:<tag> ...`, `task deploy:run IMAGE=...` and `task deploy:check`.
- **Hardened runtime.** Both containers keep the image's non-root user (1654) and run with `--read-only --tmpfs /tmp:rw,noexec,nosuid,nodev,size=64m --cap-drop ALL --security-opt no-new-privileges`. The port is bound to `127.0.0.1` unless you pass another `BIND`. No task sets `Migrations__RunOnStart`.
- **Replacement downtime.** `deploy:run` removes an existing container named `NAME` before it starts the new one, so a single host is unavailable for a few seconds.
- **Explicit inputs.** Task also reads variables from your shell environment, so an exported `PORT`, `NAME`, `TAG`, `BIND` or `URL` changes the defaults. Pass them on the command line.

Example, with the database reachable on the Docker network `<net>` and both env files outside the repository:

```bash
task deploy:release TAG=sha-abc1234 ENV_FILE=/etc/acme-service/app.env \
  MIGRATE_ENV_FILE=/etc/acme-service/migrate.env NETWORK=<net>
```

To rehearse against the local Compose stack (`task up` first), use `NETWORK=acme-service_default PORT=18080 NAME=acme-service-verify` so the release does not clash with the Compose `app`, and remove the container afterwards with `docker rm -f acme-service-verify`.

Rollback with task follows [Rollback](#rollback): run only `task deploy:run TAG=<previous tag> ENV_FILE=<file>` (with the previous release's environment values), then `task deploy:check`. Do not run `deploy:migrate` or `deploy:release` for the old tag.

## Deploy order

The web host composition never migrates (INV-16). Every deployment runs the migration first and starts the new app only when it succeeded; on Render, `Migrations__RunOnStart=true` makes the container do this at start, and elsewhere you run the one-shot container above.

- The migration runs every module's EF migrations in registration order under one PostgreSQL advisory lock, then the Hangfire schema (`Jobs__Provider=Hangfire` must be set for the run), then the Auth permission and role seeder. It is idempotent. Allow it up to 15 minutes; see [migrations](migrations.md#time-limit).
- If the migration exits non-zero the deployment stops before the app is touched (on Render the new container exits 1 before any port opens); the running instances keep serving. Read the migration output.
- With `Migrations__RunOnStart=true`, replicas that start together are serialised by the advisory lock, and schema changes must stay backward compatible with the old version, which keeps serving during the rollout. The service environment holds the DDL string; the process unsets it after a successful migration, and the real separation is the Postgres roles.
- Whether the previous instance keeps serving while a new one starts depends on your platform (Render does this by default). A new instance only becomes ready when `/health/ready` passes, and readiness fails while any module has pending migrations, so an instance started before the migration finished waits instead of serving.
- Configuration is read at startup: a configuration-only change needs a redeploy or restart of every instance.
- Apply only revisions that are meant to be released.

## Probes and runtime

| Probe | Path | Meaning |
| --- | --- | --- |
| startup | `/health/live` | Allow up to 2 minutes to start. |
| liveness | `/health/live` | The process only; a dependency outage never needs a restart. |
| readiness | `/health/ready` | PostgreSQL reachable and no pending migrations; Redis reports Degraded but stays 200. |

The container runs as user 1654 with a read-only root filesystem (a writable `/tmp`). The chiseled runtime image has no general-purpose shell, so health checks should be HTTP requests from outside the container and debugging uses a sidecar or ephemeral container of your platform ([troubleshooting](troubleshooting.md#debugging-the-container)). Of the verbs, only `auth:grant-role` can run in the app container (it needs `ConnectionStrings:Postgres`, which the app has); `migrate` and `migrate --list` need `ConnectionStrings:PostgresMigrations`, so run them as a one-shot job/container (on Render, `Migrations__RunOnStart=true` runs the migration at container start). Put a reverse proxy or load balancer in front and terminate TLS there (Render does both).

## After the first deployment

1. Register the first user through the API, confirm the email, then grant the Admin role ([auth guide](../guides/auth.md#bootstrap-the-first-administrator)):

   ```bash
   dotnet Acme.Service.Host.dll auth:grant-role --email <email> --role Admin
   ```

   On Render run it from the service's Shell tab or as a one-off job; on another Docker host use `docker exec <container> dotnet Acme.Service.Host.dll auth:grant-role ...`.

   `exec` and the Shell tab start a second .NET process inside the running instance, sharing its memory limit. The command host is small, but an instance already near its limit can be OOM-killed; run it on a lightly loaded instance, or as a one-off job (it needs only `ConnectionStrings__Postgres`).

2. Check that `/health/ready` is 200 through the proxy, that the startup log shows the expected `Provider ... = ...` lines, and that there is no forwarded-headers warning.

## Expand/contract migrations

Old and new instances can run side by side during a replacement, and the migration runs before any new instance exists, so **every migration must work with the previous release's code**:

1. **Expand** (release N): add tables, nullable columns or columns with defaults, and indexes. Code writes both the old and the new shape if both are read.
2. **Migrate data**: backfill idempotently, in the migration or a background job.
3. **Contract** (release N+1 or later, once no running version reads the old shape): drop old columns or tables, add `NOT NULL`, narrow types.

Never rename or drop in the release that stops using something. Rollback is "redeploy the previous image"; there are no down migrations in the pipeline, which is safe only because of this rule. Details and examples: [migrations](migrations.md).

## Rollback

1. Redeploy the previous image tag (`sha-<short>` or a version tag) for the app. Do not re-run the old release's migration: it would apply nothing (EF ignores applied migrations it does not know), but its seeder would reset the permission catalog to the old release's declarations, deleting permissions the newer release added (and their grants) until the next `migrate`.
2. The old instances pass readiness because the expand-only schema is compatible and the old code sees no pending migrations of its own.
3. If a contract migration already ran, roll forward instead: restore from backup is the only way back.

Caveats:

- After step 1 the app runs an older tag than the last migration. That is intended until the next forward deployment, whose migration run then applies the newer release's migrations and seeder.
- Roll the deployment's environment values back together with the image, from the same revision; new keys the old release does not know are harmless, but a value the new release changed may not suit the old one.
- The old release validates its own configuration at startup: if the newer release removed or renamed a key the old one requires, old instances fail startup (and the newer ones keep serving if your platform replaces gradually). Restore the key before rolling back.

## Docker Compose is not a production deployment

`deploy/compose/compose.yaml` binds every port to `127.0.0.1`, runs single instances without backups, uses the public Azurite development key and an unauthenticated Aspire dashboard. Use it for development and demos only.
