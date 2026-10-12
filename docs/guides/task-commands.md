# Task commands

> Applies to: Acme.Service template

The root `Taskfile.yml` wraps the build, test, database, Docker and deploy commands of this repository in short, named tasks for [Task](https://taskfile.dev) (go-task, binary `task`). It is a convenience only: CI (`.github/workflows/ci.yml`) and the Docker image call the raw commands, and every task below lists the raw command it runs, so you can always work without `task`.

## Prerequisites

`task` is not a .NET tool; install it once per machine:

| OS | Install |
| --- | --- |
| macOS | `brew install go-task` |
| Windows | `winget install Task.Task` |
| Others | see [taskfile.dev/installation](https://taskfile.dev/installation) |

Then, from the repository root:

```bash
task --version   # the Taskfile is verified with Task v3.54.0
task --list      # lists every public task, with its description (plain `task` does the same)
```

Use a recent Task 3.x. Old distribution packages may not understand `requires`, `prompt` or `aliases`; if `task --list` fails to parse the Taskfile, update Task. The Taskfile uses POSIX shell only, so it is expected to work on Windows too, but it is verified on macOS only (Windows is best-effort).

## Usage

| Form | Meaning |
| --- | --- |
| `task <ns>:<name>` | Run a task, for example `task dev:build`. Namespaces: `dev`, `test`, `db`, `ci`, `init`, `docker`, `deploy`. |
| `task b`, `task t`, `task r`, `task f`, `task up`, `task down` | Aliases of `dev:build`, `test:unit`, `dev:run`, `dev:format`, `docker:up`, `docker:down`. |
| `task <name> VAR=value` | Pass an input, for example `task db:add MODULE=Auth NAME=AddWidgets`. A missing required input stops the task before anything runs. |
| `task <name> -- <args>` | Append extra arguments to the underlying command (test, run, watch, logs, `db:list` and init tasks), for example `task dev:run -- --urls http://localhost:5080`. |
| `task --dry <name>` | Print the commands without running them. Silent tasks (for example the `test*` tasks, `dev:run`, `db:migrate` and `docker:logs`) print nothing under a plain `--dry`: use `task --dry --verbose <name>` or `task --summary <name>`. |
| `task --yes <name>` | Answer the confirmation prompt (only `docker:reset` asks). |

Inputs are Task variables, and Task also reads variables of the same name from your shell environment. An exported `PORT`, `NAME`, `TAG`, `BIND` or `URL` therefore becomes the default of the deploy tasks; pass these inputs explicitly on the command line.

## dev: setup, build, run, format

| Task | Alias | Inputs | Runs | Notes |
| --- | --- | --- | --- | --- |
| `dev:setup` | | | `dotnet tool restore`, then `dev:restore`, `dev:env`, `docker compose -f deploy/compose/compose.yaml up -d --wait postgres` | First-time setup. Then run the printed `dotnet user-secrets set ...` commands, `task db:migrate` and `task r`. |
| `dev:env` | | | `dotnet run scripts/dev-env.cs` | Skipped when `deploy/compose/.env` exists, so it never overwrites it. Regenerate by hand with `dotnet run scripts/dev-env.cs -- --force`. |
| `dev:restore` | | | `dotnet restore Acme.Service.slnx --locked-mode` | Same as CI; never rewrites lock files. After a deliberate package change run `dotnet restore Acme.Service.slnx --force-evaluate` yourself. |
| `dev:build` | `b` | `CONFIG` (default `Release`) | `dotnet build Acme.Service.slnx -c Release` | Warnings are errors. |
| `dev:run` | `r` | `-- <args>` | `dotnet run --project src/Host/Acme.Service.Host` | Development launch profile on `http://localhost:8080`. |
| `dev:watch` | | `-- <args>` | `dotnet watch run --project src/Host/Acme.Service.Host` | Hot reload. |
| `dev:format` | `f` | | `dotnet format Acme.Service.slnx` | Fixes files in place. |
| `dev:format:check` | | | `dotnet format Acme.Service.slnx --verify-no-changes` | Changes nothing. |
| `dev:clean` | | `CONFIG` | `dotnet clean Acme.Service.slnx -c Release` | |

## test: unit, integration, architecture, coverage

Every test task accepts extra `dotnet test` arguments after `--`.

| Task | Alias | Inputs | Runs | Notes |
| --- | --- | --- | --- | --- |
| `test` | | | `dotnet test Acme.Service.slnx -c Release` | Everything; needs a running Docker. |
| `test:unit` | `t` | | `dotnet test Acme.Service.slnx -c Release --filter "Category!=Integration"` | Unit and architecture tests, no Docker. |
| `test:integration` | | | `dotnet test Acme.Service.slnx -c Release --filter "Category=Integration"` | Needs a running Docker (Testcontainers). |
| `test:arch` | | | `dotnet test Acme.Service.slnx -c Release --filter "FullyQualifiedName~ArchitectureTests"` | Includes the template token test. |
| `test:one` | | `PROJECT` | `dotnet test <PROJECT> -c Release` | For example `task test:one PROJECT=tests/Acme.Service.Modules.Auth.Tests`. |
| `test:cov` | | | `dotnet test Acme.Service.slnx -c Release --filter "Category!=Integration" --collect "XPlat Code Coverage" --results-directory TestResults/coverage` | Output under the gitignored `TestResults/`. |

`test`, `test:unit`, `test:integration` and `test:arch` already pass a `--filter`, so do not add another one after `--`. To narrow a run to one class or method, filter inside one project with `task test:one PROJECT=tests/Acme.Service.Modules.Auth.Tests -- --filter "FullyQualifiedName~ChangePasswordTests"`, or use the raw `dotnet test Acme.Service.slnx -c Release --filter "FullyQualifiedName~ChangePasswordTests"`.

## db: migrations and the local migrate verb

`db:add`, `db:list`, `db:remove` and `db:script` need `dotnet ef` (`task dev:setup` restores it) and a `MODULE` that is a folder under `src/Modules/`. They pass `--project src/Modules/<MODULE>/Acme.Service.Modules.<MODULE> --startup-project src/Host/Acme.Service.Host --context <MODULE>DbContext` (written `<EF options>` below).

| Task | Inputs | Runs | Notes |
| --- | --- | --- | --- |
| `db:add` | `MODULE`, `NAME` | `dotnet ef migrations add <NAME> <EF options> --output-dir Persistence/Migrations` | No database needed. Then make the migration, designer and snapshot classes `internal` and run `task test:arch`. |
| `db:list` | `MODULE`, `-- <args>` | `dotnet ef migrations list <EF options> --no-connect` | No database needed; EF's closing `Pending status not shown...` line is expected. |
| `db:remove` | `MODULE` | `dotnet ef migrations remove <EF options>` | Connects through `ConnectionStrings__PostgresMigrations`; re-check the snapshot's `internal` modifier afterwards. |
| `db:script` | `MODULE`, `OUT` (default `artifacts/sql/<MODULE>.sql`) | `dotnet ef migrations script --idempotent <EF options> --output <OUT>` | `artifacts/` is gitignored. |
| `db:migrate` | | `DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate` | Every module's migrations, the Hangfire schema when configured and the Auth seed. Task sets `DOTNET_ENVIRONMENT` itself, on every OS. |
| `db:migrate:list` | | `DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate --list` | Read-only. |

## ci: local mirror of the CI checks

| Task | Runs | Notes |
| --- | --- | --- |
| `ci` | `dotnet restore Acme.Service.slnx --locked-mode`, `dotnet build Acme.Service.slnx -c Release --no-restore`, `dotnet format Acme.Service.slnx --verify-no-changes --no-restore`, `dotnet test Acme.Service.slnx -c Release --no-build --filter "Category!=Integration"`, then `ci:vulnerable` and `ci:docs` | Stops at the first failing step. Always Release, whatever `CONFIG` says. |
| `ci:integration` | `dotnet test Acme.Service.slnx -c Release --filter "Category=Integration"` | Needs a running Docker. |
| `ci:vulnerable` | `bash scripts/ci/check-vulnerable.sh Acme.Service.slnx` | Needs `jq` and a restored solution. |
| `ci:docs` | `bash scripts/ci/check-doc-links.sh README.md docs src/Modules/Auth/README.md src/BuildingBlocks`, then `bash scripts/ci/check-config-keys.sh` | Also checks the sample module's README while it exists. |

`task ci` mirrors the build-test job of `ci.yml` without the integration tests (run `task ci:integration` for those) and adds the doc checks, which CI itself does not run. CI does not use `task`; it runs the raw commands.

## init: turn the template into your service

| Task | Inputs | Runs | Notes |
| --- | --- | --- | --- |
| `init:dry` | `NAME`, `-- <flags>` | `dotnet run scripts/init.cs -- --name <NAME> --dry-run` | Preview; writes nothing. |
| `init` | `NAME`, `-- <flags>` | `dotnet run scripts/init.cs -- --name <NAME>` | Rewrites the repository in place, `Taskfile.yml` included. Run it on a fresh, clean clone. Extra flags after `--`, for example `task init NAME=Contoso.Billing -- --remove-sample`. |

## docker: the local Compose stack

Every `docker:*` task needs `deploy/compose/.env` (otherwise it stops with "run task dev:setup") and a running Docker. They all run `docker compose -f deploy/compose/compose.yaml ...` (written `compose` below).

| Task | Alias | Inputs | Runs | Notes |
| --- | --- | --- | --- | --- |
| `docker:up` | `up` | | `compose up -d --build` | Full stack: PostgreSQL, Redis, RustFS, Azurite, Mailpit, one-shot `migrate`, app on `http://localhost:8080`. |
| `docker:up:infra` | | | `compose up -d --wait postgres` | All that `task dev:run` needs. |
| `docker:obs` | | | `compose --profile observability up -d --build` | Adds the Aspire dashboard (`http://localhost:18888`); set `OTLP_ENDPOINT=http://aspire-dashboard:18889` in `deploy/compose/.env`. |
| `docker:down` | `down` | | `compose down` | Keeps the data volumes. |
| `docker:reset` | | | `compose down -v` | Deletes the data volumes after a prompt; `task --yes docker:reset` skips it. |
| `docker:logs` | | `SVC` (default: all), `-- <args>` | `compose logs --tail 100 [SVC]` | Follow with `task docker:logs SVC=app -- -f`. |
| `docker:ps` | | | `compose ps` | Containers and health. |
| `docker:migrate` | | | `compose run --rm migrate` | Re-runs the one-shot migrate service (idempotent). |
| `docker:grant-admin` | | `EMAIL` | `compose run --rm migrate auth:grant-role --email <EMAIL> --role Admin` | The user must have registered first. |

## deploy: a single Docker host

Render, the primary target, deploys on push and needs none of these tasks; `task deploy:render` only prints its checklist. The other deploy tasks build, migrate, run and check the image on one Docker host, or rehearse that locally. The procedure, the env-file rules and rollback are in [deployment](../operations/deployment.md#deploy-with-task).

| Task | Inputs | Runs | Notes |
| --- | --- | --- | --- |
| `deploy:image` | `TAG` | `docker build -f Dockerfile -t acme-service:<TAG> .` | Then fails unless the image user is non-root. |
| `deploy:migrate` | `TAG` or `IMAGE`; `ENV_FILE` or `MIGRATE_ENV_FILE`; `NETWORK` | `docker run --rm ... --env-file <MIGRATE_ENV_FILE> -e Jobs__Provider=Hangfire -e DOTNET_ENVIRONMENT=Production <image> migrate` | One-shot migration; a non-zero exit fails the task. |
| `deploy:run` | `TAG` or `IMAGE`; `ENV_FILE`; `NAME`, `PORT`, `BIND`, `NETWORK` | `docker run -d --name <NAME> --restart unless-stopped ... --env-file <ENV_FILE> -p <BIND>:<PORT>:8080 <image>` | Replaces an existing container `NAME` (a few seconds of downtime). |
| `deploy:check` | `NAME`, `PORT`, `BIND`, `URL` | `curl` against `<URL>/health/ready` until it returns 200 | Fails at once if the container is not running; waits 2 to 3.5 minutes at most; prints `docker logs --tail 50 <NAME>` on failure. |
| `deploy:release` | `TAG`, `ENV_FILE`, optional `MIGRATE_ENV_FILE`, `NETWORK`, `PORT`, `BIND`, `NAME`, `URL` | `deploy:image`, `deploy:migrate`, `deploy:run`, `deploy:check`, in that order | A failed migration stops the run before the running app is touched. |
| `deploy:render` | | prints the Render checklist | No network calls. |

Defaults: `NAME=acme-service`, `PORT=8080`, `BIND=127.0.0.1`, `URL=http://127.0.0.1:<PORT>`, `MIGRATE_ENV_FILE=<ENV_FILE>`. `deploy:migrate` and `deploy:run` add `--pull never --read-only --tmpfs /tmp:rw,noexec,nosuid,nodev,size=64m --cap-drop ALL --security-opt no-new-privileges` and keep the image's non-root user (1654). Tags must be exact (`sha-<short>` or a version); empty, `latest` and `main` are rejected. Do not use `TAG=local`: it overwrites the Compose image `acme-service:local`.

## Common workflows

First clone, run the Host with `dotnet run`:

```bash
task dev:setup        # tools, locked restore, deploy/compose/.env, PostgreSQL
# run the `dotnet user-secrets set ...` commands printed by dev-env (first generation only)
task db:migrate
task r                # http://localhost:8080, Scalar UI at /scalar
```

Full stack in Docker Compose:

```bash
task up
curl -s -o /dev/null -w '%{http_code}\n' localhost:8080/health/ready   # 200 once migrated
task down
```

New migration:

```bash
task db:add MODULE=Auth NAME=AddWidgets
# make the generated migration, designer and snapshot classes internal
task test:arch
task db:migrate
```

Before a pull request:

```bash
task ci
task ci:integration   # needs Docker
```

Without `task`, use the raw commands in the tables above, the [getting started guide](getting-started.md) or the [command cookbook](../cookbook.md).
