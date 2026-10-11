# Command cookbook

> Applies to: Acme.Service template

Every command in this document was executed once on a fresh copy of the repository (a new directory with a single commit and a clean `git status`), and its outcome is the "Expected output" you see. The results table is in the project's verification log. Commands run from the repository root unless stated. Placeholders such as `<Id>` or `<proj>` are yours to fill in; never paste real secrets into documents, tickets or shell history.

Contents: [tools](#tools) | [packages](#packages-under-central-package-management) | [EF migrations](#ef-core-migrations) | [tests](#tests) | [formatting](#formatting) | [build and publish](#build-and-publish) | [local run](#local-run) | [Docker and Compose](#docker-and-compose) | [template init](#template-init) | [auth operations](#auth-operations) | [Kubernetes and CI checks](#kubernetes-and-ci-checks)

## Tools

### Restore the pinned local tools

Purpose: install `dotnet-ef` at the version pinned in `.config/dotnet-tools.json`. Needed once per clone, before any `dotnet ef` command.

```bash
dotnet tool restore
```

Expected output: `Tool 'dotnet-ef' (version '10.0.12') was restored. Available commands: dotnet-ef` and `Restore was successful.`

Common errors:

- `Cannot find a manifest file` or `Could not execute because the specified command or file was not found` for `dotnet ef`: you are not in the repository root, or the restore was not run.
- NuGet source unreachable: check network and `nuget.config`.

Optional host tools used by the CI checks below are not .NET tools: `kubectl` (or `kustomize`), `kubeconform`, `actionlint`, `gitleaks`, `jq`, `docker`. Each entry says how to run it without installing anything but Docker.

## Packages under Central Package Management

Versions live only in `Directory.Packages.props` (`PackageVersion`); a `PackageReference` in a project has no `Version` (INV-20). Lock files (`packages.lock.json`) are committed and CI restores with `--locked-mode`, so every package change must also commit the regenerated lock files.

### Add a package

Purpose: reference a new package from one project.

```bash
dotnet add src/BuildingBlocks/Acme.Service.BuildingBlocks/Acme.Service.BuildingBlocks.csproj package Humanizer.Core
```

`dotnet package add Humanizer.Core --project <proj>` (the noun-first form of the SDK 10) behaves identically.

Expected output: `PackageReference for package 'Humanizer.Core' added to '<proj>' and PackageVersion added to central package management file '.../Directory.Packages.props'`, then `Writing packages lock file at disk`. `git status` shows `Directory.Packages.props`, the project file and the lock files of every project that depends on it.

Manual alternative, with the same result: add `<PackageVersion Include="Humanizer.Core" Version="<version>" />` to `Directory.Packages.props` (inside the group that matches the project's layer), add `<PackageReference Include="Humanizer.Core" />` (no `Version`) to the project, then:

```bash
dotnet restore Acme.Service.slnx --force-evaluate
```

`--force-evaluate` rewrites the lock files. Expected: one `Restored ...` line per project, exit 0.

Common errors:

- `NU1008: Projects that use central package version management should not define the version on the PackageReference`: remove the `Version` attribute.
- `NU1010: The following PackageReference items do not define a corresponding PackageVersion`: add the `PackageVersion` line.
- `NU1004` or `NU1403` in CI: a lock file is stale; run the force-evaluate restore and commit the lock files.
- Known side effect: `dotnet add` and `dotnet remove` re-serialise `Directory.Packages.props` (blank lines between groups are dropped and the final newline is removed). Restore the formatting by hand before committing if you care about a minimal diff, or prefer the manual edit.
- Prerelease versions are not allowed.
- The package licence must be recorded in the package ledger and must not be on the banned list (INV-25).

### Update a package

Purpose: move a package to another version everywhere at once.

```bash
dotnet list Acme.Service.slnx package --outdated
# edit the Version in Directory.Packages.props, then:
dotnet restore Acme.Service.slnx --force-evaluate
dotnet build Acme.Service.slnx -c Release --no-restore
```

Expected output: `--outdated` prints a table per project with `Requested`, `Resolved` and `Latest` (rows only for packages with a newer version; empty when everything is current). After the edit the lock files of every dependent project change to the new `resolved` version, then `dotnet restore Acme.Service.slnx --locked-mode` succeeds.

Common errors: `NU1605` (package downgrade) when another package needs a higher version: raise that one too, or pin the transitive package in `Directory.Packages.props`. Keep packages of one family (EF Core, ASP.NET Core, Npgsql provider) on the same major version.

### Remove a package

Purpose: drop a package from one project and from the central list when nothing else uses it.

```bash
dotnet remove src/BuildingBlocks/Acme.Service.BuildingBlocks/Acme.Service.BuildingBlocks.csproj package Humanizer.Core
# CPM keeps the PackageVersion: delete its line from Directory.Packages.props if no other project references it, then
dotnet restore Acme.Service.slnx --force-evaluate
```

Expected output: `Removing PackageReference for package 'Humanizer.Core' from project '<proj>'`. After the `PackageVersion` is deleted and the force-evaluate restore ran, the lock files return to their previous content (the verified round trip add, update, remove leaves only the re-serialised `Directory.Packages.props`).

Common error: leaving the `PackageVersion` behind is harmless to the build but leaves an unused version to maintain; reviewers should reject it.

### Check for vulnerable packages

Purpose: list packages with known advisories, direct and transitive. CI fails on High and Critical.

```bash
dotnet list Acme.Service.slnx package --vulnerable --include-transitive
scripts/ci/check-vulnerable.sh        # the CI form; needs jq
```

Expected output: `The given project '<name>' has no vulnerable packages given the current sources.` per project, and `No vulnerable packages found.` from the script (exit 0). The script exits 1 for High/Critical findings and 2 when the scan itself failed (for example no network or no restore), failing closed.

Common errors: exit 2 with `jq is not installed` (`brew install jq`); a vulnerable transitive package is fixed by pinning a patched version in `Directory.Packages.props`.

## EF Core migrations

Each module has its own context, schema and migrations. Examples use the Auth module; substitute your module's project and `<Name>DbContext`. Details and review rules: [migrations runbook](operations/migrations.md).

Shared options (shown once):

```bash
EFOPTS="--project src/Modules/Auth/Acme.Service.Modules.Auth --startup-project src/Host/Acme.Service.Host --context AuthDbContext"
```

The commands below spell the options out so that you can copy them directly; `$EFOPTS` stands for the line above.

Commands that connect to the database read `ConnectionStrings__PostgresMigrations` from the environment. Without it they try `Host=localhost;Port=5432;Database=acme_service;Username=postgres` with no password and fail with `Failed to connect to 127.0.0.1:5432` or an authentication error. Export the value from your user secrets or `.env` without echoing it (`export ConnectionStrings__PostgresMigrations=...`).

### Add a migration

```bash
dotnet ef migrations add <MigrationName> $EFOPTS --output-dir Persistence/Migrations
```

Expected output: `Build started...`, `Build succeeded.`, `Done. To undo this action, use 'ef migrations remove'`. New files: `Persistence/Migrations/<timestamp>_<MigrationName>.cs` and `.Designer.cs`; the model snapshot is rewritten. The command does not connect to a database.

Required follow-up: the generated migration, designer and snapshot classes are `public` (the snapshot loses its `internal` modifier); make all of them `internal`, otherwise architecture test R5 fails. Review the SQL (below).

Common errors: `Unable to create a 'DbContext'` or a missing design-time factory: the `--project` and `--context` do not match; `Build failed`: fix compile errors first; `The name '<x>' is used by an existing migration`.

### List migrations

```bash
dotnet ef migrations list $EFOPTS --no-connect      # names only, no database needed
dotnet ef migrations list $EFOPTS                   # with (Pending) status, needs the database
```

Expected output: one migration id per line; the connected form appends `(Pending)` to unapplied ones. `--no-connect` ends with `Pending status not shown. Unable to determine which migrations have been applied...`, which is expected.

### Remove the last migration

```bash
dotnet ef migrations remove $EFOPTS
```

Expected output: `Removing migration '<id>'.`, `Reverting the model snapshot.`, `Done.` Only an unapplied migration can be removed; for an applied one the command exits 1 with `The migration '<id>' has already been applied to the database. Revert it and try again.` The command connects to the database to check that, so it also fails with `Failed to connect` when PostgreSQL is not reachable. After removing, run `git diff` on the snapshot and restore its `internal` modifier if it changed.

### Idempotent SQL script

Purpose: SQL for review, or for a DBA to apply.

```bash
dotnet ef migrations script --idempotent $EFOPTS --output auth.sql
```

Expected output: the script starts with `CREATE SCHEMA` guarded by `IF NOT EXISTS`, creates `<schema>.__ef_migrations_history` and wraps each migration in an `IF NOT EXISTS(SELECT 1 FROM <schema>.__ef_migrations_history ...)` block. Write the file outside the repository or delete it afterwards. The script is UTF-8 with a byte-order mark; tell `psql` nothing special, but strip the BOM for tools that choke on it.

### Apply migrations locally

Purpose: apply every module's migrations, the Hangfire schema (when configured) and the Auth seed, exactly as the Compose service and the Kubernetes Job do. The web app never migrates.

```bash
docker compose -f deploy/compose/compose.yaml up -d --wait postgres
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate
```

Expected output (Development also prints many `DBG` lines; look at the last ones): `Auth seed complete: 7 permission(s), 3 seed role(s), 24 change(s)` on a new database, `0 change(s)` on a repeat, and `Migration completed for 2 module context(s)`. Exit 0. The run is idempotent.

Common errors:

- `System.InvalidOperationException: ConnectionStrings:PostgresMigrations is not configured.` with exit 1: the verbs read `DOTNET_ENVIRONMENT` (or `--environment=Development`), not `ASPNETCORE_ENVIRONMENT`; without it the run is `Production` and ignores `appsettings.Development.json` and user secrets. Set the user secrets ([local run](#user-secrets-for-a-local-run)) first.
- `password authentication failed` / `Failed to connect`: PostgreSQL is not up, or the password in the secrets differs from the one the volume was created with ([reset volumes](#reset-volumes-start-from-an-empty-database)).

### List pending migrations

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate --list
```

Expected output (after the `DBG` lines): `auth (AuthDbContext): 4 pending` with each migration id indented below, one line per module context; after applying, `auth (AuthDbContext): 0 pending`. Exit 0, nothing is changed.

## Tests

Test docs: [testing guide](guides/testing.md). Integration tests start Testcontainers, so Docker must be running; unit and architecture tests need no Docker.

| Purpose | Command |
| --- | --- |
| Everything | `dotnet test Acme.Service.slnx -c Release` |
| Unit and architecture only | `dotnet test Acme.Service.slnx -c Release --filter "Category!=Integration"` |
| Integration only | `dotnet test Acme.Service.slnx -c Release --filter "Category=Integration"` |
| One project | `dotnet test tests/Acme.Service.Modules.Auth.Tests -c Release` |
| Architecture rules | `dotnet test Acme.Service.slnx -c Release --filter "FullyQualifiedName~ArchitectureTests"` |
| One test class or method | `dotnet test Acme.Service.slnx -c Release --filter "FullyQualifiedName~RefreshTokenTests"` |
| With coverage | `dotnet test Acme.Service.slnx -c Release --filter "Category!=Integration" --collect "XPlat Code Coverage"` |

Add `--no-build` after a Release build to skip recompiling.

Expected output: one `Passed!  - Failed: 0, Passed: <n>, Skipped: 0, Total: <n>, Duration: ...` line per test assembly (unit run: Auth 194, BuildingBlocks 254, Infra 325, Architecture 23 and so on; the integration run adds the Docker-backed tests, roughly 3 to 4 minutes the first time while images are pulled). With a filter, assemblies without a match print `No test matches the given testcase filter` and do not fail the run, so the line to look for is the `Passed!` of the assembly you meant. Coverage writes `tests/<project>/TestResults/<guid>/coverage.cobertura.xml` (gitignored); the paths are listed under `Attachments:`.

Common errors:

- Integration tests fail at start with a Docker connection error: start Docker.
- `Failed!` lines print the test name and assertion; rerun one with the single-test filter. Flaky container start-up in CI is retried once and reported.
- `--no-build` only works after a build of the same configuration exists (`-c Release`); otherwise `dotnet test` finds no assembly.

## Formatting

Purpose: style and analyzer rules from `.editorconfig`; CI runs the verify form (INV-21).

```bash
dotnet format Acme.Service.slnx --no-restore                        # fix in place
dotnet format Acme.Service.slnx --verify-no-changes --no-restore    # CI form
```

Expected output: no output and exit 0 when the tree is already formatted; the verify form prints one `error WHITESPACE: Fix whitespace formatting` (or similar) line per offending file and exits 2 when anything would change. Common error: restore failures when you omit `--no-restore` while offline; run `dotnet restore Acme.Service.slnx` first.

## Build and publish

```bash
dotnet restore Acme.Service.slnx --locked-mode
dotnet build Acme.Service.slnx -c Release --no-restore
dotnet publish src/Host/Acme.Service.Host -c Release -o out
```

Expected output: `Build succeeded.` with `0 Warning(s)` and `0 Error(s)` (warnings are errors); the publish writes the Host and its dependencies to `out/` (gitignored), ending with `Acme.Service.Host -> .../out/`. Common errors: `NU1004` (lock file inconsistent) with `--locked-mode` after a package change (run the force-evaluate restore); any analyzer warning fails the build until fixed or justified in `.editorconfig`.

## Local run

### Generate the development environment

Purpose: create `deploy/compose/.env` (gitignored, mode 0600) with a fresh RSA-3072 signing key and random database, Redis and S3 passwords.

```bash
dotnet run scripts/dev-env.cs
dotnet run scripts/dev-env.cs -- --force     # regenerate over an existing file
```

Expected output: `Wrote .../deploy/compose/.env (signing key 'dev-<date>-<id>', RSA-3072).` followed by five `dotnet user-secrets set ... --project src/Host/Acme.Service.Host` commands that contain secrets: run them in a private terminal, never paste them anywhere. Exit 0. Common errors: exit 1 with `.env already exists; nothing was changed` (use `--force`, which also invalidates the data volume's database password, see [reset volumes](#reset-volumes-start-from-an-empty-database)); exit 2 with `Unknown argument '<x>'` for bad arguments. The first run compiles a file-based app and takes a few seconds.

### User secrets for a local run

Purpose: give `dotnet run` the same signing key and connection strings without putting them in files of the repository. Secrets are stored per user under the project's `UserSecretsId`.

```bash
# run the five commands printed by scripts/dev-env.cs (they end in --project src/Host/Acme.Service.Host), then:
dotnet user-secrets list --project src/Host/Acme.Service.Host      # check; values are shown, keep it private
dotnet user-secrets clear --project src/Host/Acme.Service.Host     # remove everything again
```

Expected output: `Successfully saved <key> to the secret store.` for each; `clear` prints nothing and a following `list` prints `No secrets configured for this application.` The keys are `Auth:Jwt:SigningKeys:0:Kid`, `:Status`, `:PrivateKeyBase64`, `ConnectionStrings:Postgres` and `ConnectionStrings:PostgresMigrations`. User secrets are read only in the Development environment.

### Run the Host

```bash
docker compose -f deploy/compose/compose.yaml up -d --wait postgres
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate
dotnet run --project src/Host/Acme.Service.Host
curl -s -o /dev/null -w '%{http_code}\n' localhost:8080/health/ready
```

Expected output: the host listens on `http://localhost:8080` (launch profile, `ASPNETCORE_ENVIRONMENT=Development`); `/health/ready` and `/health/live` return `200`, `/openapi/v1.json` returns `200`, `/scalar` redirects (`302`), any other route without a token returns `401`. Stop with Ctrl+C. Common errors: `/health/ready` is `503` until `migrate` has run; `Address already in use` when port 8080 is taken (`--urls http://localhost:<port>`); startup fails with a signing key error when the user secrets are missing. Full first-call walkthrough: [getting started](guides/getting-started.md).

## Docker and Compose

The Compose file `deploy/compose/compose.yaml` needs `deploy/compose/.env` ([generate it](#generate-the-development-environment)); every command fails with `required variable POSTGRES_PASSWORD is missing a value: run 'dotnet run scripts/dev-env.cs' first` without it. Examples use `C="docker compose -f deploy/compose/compose.yaml"`; write the full command out.

### Build the image

```bash
docker build -t acme-service:local .
docker image inspect --format '{{.Config.User}}' acme-service:local
```

Expected output: a multi-stage build ending with `naming to docker.io/library/acme-service:local`, exit 0 (the first build pulls the SDK and runtime images and restores packages; later builds reuse the cache). The inspect command prints `1654`, the non-root user. Common errors: `failed to resolve source metadata` (no network); `NU1004` locked-mode restore errors when lock files are stale in the working tree; `--no-cache` forces a clean build.

### Start, stop, logs

```bash
docker compose -f deploy/compose/compose.yaml up -d --build
curl -s -o /dev/null -w '%{http_code}\n' localhost:8080/health/ready
docker compose -f deploy/compose/compose.yaml logs --tail 20 app
docker compose -f deploy/compose/compose.yaml logs -f app
docker compose -f deploy/compose/compose.yaml down
```

Expected output: `up` creates the network, volumes and containers for PostgreSQL, Redis, RustFS, Azurite, Mailpit, the one-shot `migrate` and the app, ending with `Container acme-service-app-1 Started`; the app starts only after `migrate` exited 0. `/health/ready` prints `200` within a few seconds, an unauthenticated API call prints `401`, and Mailpit answers on `http://localhost:8025`. App logs are JSON lines (`@t`, `@m`, `RequestPath`, `StatusCode`, `CorrelationId`); `logs -f` follows until Ctrl+C. `down` removes the containers and network but keeps the data volumes.

Common errors: `port is already allocated` (change `APP_PORT`, `POSTGRES_PORT` and the other `*_PORT` values in `.env`); `/health/ready` stays `503` while `migrate` failed: `docker compose -f deploy/compose/compose.yaml logs migrate`.

### Rebuild after code changes

```bash
docker compose -f deploy/compose/compose.yaml up -d --build app
docker compose -f deploy/compose/compose.yaml restart app     # same image, new environment or secrets
```

Expected output: the image is rebuilt (cached layers are reused until a project file, lock file or source changes) and `Container acme-service-app-1 Started`; wait a few seconds for readiness. `app` depends on `migrate` having exited 0; schema changes in a new build are applied by re-running it as shown in the next entry (it is idempotent).

### Run migrate (and other verbs) in Compose

```bash
docker compose -f deploy/compose/compose.yaml run --rm migrate
docker compose -f deploy/compose/compose.yaml run --rm migrate migrate --list
docker compose -f deploy/compose/compose.yaml run --rm migrate auth:grant-role --email <email> --role Admin
```

Expected output: the same log lines as the local run without the debug noise (`Hangfire schema hangfire is up to date`, `Auth seed complete: ... 0 change(s)`, `Migration completed for 2 module context(s)`); `--list` prints `auth (AuthDbContext): 0 pending`. The service's default command is `migrate`, so a different verb replaces it entirely and must be spelled out (`migrate migrate --list`). The image may print `Cannot load library libgssapi_krb5.so.2` or `Error: libgssapi_krb5.so.2: cannot open shared object file`: this is a harmless Kerberos probe by the PostgreSQL driver in the minimal runtime image; the exit code is what counts.

### Reset volumes (start from an empty database)

```bash
docker compose -f deploy/compose/compose.yaml down -v
```

Expected output: the containers, the network and the three volumes (`acme-service_postgres-data`, `acme-service_rustfs-data`, `acme-service_azurite-data`) are removed; `docker volume ls | grep acme-service` prints nothing. This destroys all local data. Use it after regenerating `.env` with `--force`, because the PostgreSQL volume keeps the old password (symptom: `password authentication failed for user "postgres"`).

## Template init

Purpose: turn a clean clone of the template into your own service. Run on a fresh clone with a clean working tree (the script refuses otherwise unless `--force`). Name rule: dotted PascalCase such as `Contoso.Billing`.

```bash
dotnet run scripts/init.cs -- --name Contoso.Billing --dry-run          # preview, writes nothing
dotnet run scripts/init.cs -- --name Contoso.Billing                    # variant A: keep the Orders sample
dotnet run scripts/init.cs -- --name Contoso.Billing --remove-sample    # variant B: Auth only
```

Then follow the printed next steps:

```bash
dotnet restore Contoso.Billing.slnx --force-evaluate
dotnet format Contoso.Billing.slnx
dotnet build Contoso.Billing.slnx -c Release
dotnet test Contoso.Billing.slnx -c Release --filter "Category!=Integration"
```

Expected output: the dry run lists renames and ends with `708 files to edit (3256 token replacements, 0 sample lines), 45 renames; 0 binary files skipped.` and `Dry run: nothing was written.` The real run ends with `Done. The template is now Contoso.Billing. Next steps:`. Variant B reports `2 directories and 4 files to delete, 610 files to edit (2755 token replacements, 284 sample lines), 39 renames`. After either variant: restore, format, a Release build with 0 warnings, unit tests all passing (variant A: Orders tests included; variant B: Auth, BuildingBlocks, Infra and Architecture tests), `dotnet restore ... --locked-mode` and `dotnet format --verify-no-changes` pass, and no occurrence of the template tokens remains (INV-23). Exit codes: 0 success, 1 refusal (dirty tree or already initialized), 2 invalid name or arguments.

Common errors: exit 1 `Refusing to run: The git working tree has uncommitted changes`; exit 2 `Invalid --name '<x>'. Use PascalCase dotted segments` for a name that is not dotted PascalCase with at least two segments; skipping the force-evaluate restore fails the locked restore with a lock file mismatch, because the lock files name the projects. Also set your registry in `deploy/k8s/**/kustomization.yaml` as the script's last line says.

## Auth operations

### Grant the Admin role

Purpose: bootstrap the first administrator; there is no default account. The user must have registered first ([getting started](guides/getting-started.md#4-make-a-first-authenticated-call)).

```bash
# local dotnet run (needs the user secrets, PostgreSQL up and migrated)
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- auth:grant-role --email <email> --role Admin
# Docker Compose
docker compose -f deploy/compose/compose.yaml run --rm migrate auth:grant-role --email <email> --role Admin
```

Expected output: a warning-level security event `SecurityEvent auth.role-granted: granted role Admin to user <id>` and exit 0 on the first run; repeating it logs `User <id> already has role Admin` (or nothing but the debug lines) and exits 0. Errors: exit 1 with `No user has the given email` or an unknown role name (the seed roles are `Admin`, `Staff` and `Customer`, see [permissions](guides/permissions.md)); the verb reads `DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT`. The user must log in again to get a token with the new permissions.

### Generate a signing key for rotation

Purpose: create a new RS256 key for the key ring; the rotation procedure itself is in [key rotation](operations/key-rotation.md). Generate it outside the repository.

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out signing-key-1.pem
chmod 600 signing-key-1.pem
base64 < signing-key-1.pem | tr -d '\n' > signing-key-1.b64       # only for the PrivateKeyBase64 form
openssl pkey -in signing-key-1.pem -noout -text | head -1          # prints: Private-Key: (3072 bit, 2 primes)
```

Expected output: no output from the first three commands, exit 0; the PEM starts with `-----BEGIN PRIVATE KEY-----`; the base64 file is about 3.3 KB on one line. Common errors: `openssl: command not found` (install OpenSSL or LibreSSL). Never commit the files (gitleaks fails the build if you do); choose a unique `Kid` as described in the rotation runbook. For a local development environment it is simpler to run `dotnet run scripts/dev-env.cs -- --force`.

## Kubernetes and CI checks

### Render the overlays

Purpose: see exactly what would be applied. Overlays: `deploy/k8s/overlays/{dev,qa,prod}` (app) and `deploy/k8s/migration/overlays/{dev,qa,prod}` (migration Job).

```bash
kubectl kustomize deploy/k8s/overlays/dev
kustomize build deploy/k8s/overlays/dev          # equivalent, when kustomize is installed
kubectl kustomize deploy/k8s/migration/overlays/prod
```

Expected output: YAML documents separated by `---`: ServiceAccount, ConfigMap, Service, Deployment, PodDisruptionBudget and HorizontalPodAutoscaler for the app overlays (6 objects), ConfigMap and Job for the migration overlays (2 objects); no `Secret` objects, ever (INV-11). Exit 0 for all six overlays. Common error: `kubectl` older than 1.14 has no `kustomize` subcommand.

`kubectl apply --dry-run=client -f -` on the rendered output is not a substitute for validation: kubectl needs a reachable cluster for API discovery even in client mode, and without one it exits 1 with `couldn't get current server API group list ... connection refused`. Use the next entry instead.

### Validate the manifests

Purpose: render every overlay and validate it against the Kubernetes schemas; also fails if a `Secret` is rendered. This is the CI job `k8s-manifests`.

```bash
scripts/ci/validate-k8s.sh
```

Requires `kubeconform` and `kustomize` or `kubectl` on the `PATH`. Without installing kubeconform, put a one-line wrapper first in the `PATH`:

```bash
mkdir -p "$HOME/.local/kc-bin"
printf '#!/usr/bin/env bash\nexec docker run --rm -i ghcr.io/yannh/kubeconform:v0.8.0 "$@"\n' > "$HOME/.local/kc-bin/kubeconform"
chmod +x "$HOME/.local/kc-bin/kubeconform"
PATH="$HOME/.local/kc-bin:$PATH" scripts/ci/validate-k8s.sh
```

Expected output: for each of the six overlays `==> deploy/k8s/...` and `Summary: 6 resources found parsing stdin - Valid: 6, Invalid: 0, Errors: 0, Skipped: 0` (2 resources for the migration overlays), then `All Kubernetes overlays rendered and validated.` Exit 1 and `Kubernetes manifest validation FAILED` when something is invalid (not exercised here; taken from the script). Common errors: `error: kubeconform is not installed`; `error: neither kustomize nor kubectl is installed`; schema download failures without network.

### Lint the GitHub Actions workflows

```bash
actionlint
docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:1.7.7      # without installing actionlint
```

Expected output: nothing and exit 0 when the workflows are clean. Findings print `file:line:col: message [rule]` and exit 1, including embedded shell findings from shellcheck (for example SC2010 for `ls | grep`).

### Scan for secrets

```bash
docker run --rm -v "$PWD:/repo" zricethezav/gitleaks:v8.30.0@sha256:691af3c7c5a48b16f187ce3446d5f194838f91238f27270ed36eef6359a574d9 \
  git /repo --config /repo/.gitleaks.toml --redact --verbose --no-banner     # all commits, as in CI
docker run --rm -v "$PWD:/repo" zricethezav/gitleaks:v8.30.0@sha256:691af3c7c5a48b16f187ce3446d5f194838f91238f27270ed36eef6359a574d9 \
  dir /repo --config /repo/.gitleaks.toml --redact --no-banner               # working tree, including uncommitted files
```

Expected output: `<n> commits scanned.` or `scanned ~<bytes>` and `no leaks found`, exit 0. A finding prints the rule, file and a redacted match and exits 1: remove the secret, rotate it, and never "fix" it by allowlisting a real credential (only the public Azurite development key is allowlisted, in `.gitleaks.toml`). Common errors: the `git` mode needs the `.git` directory to be inside the mounted folder; on a shallow clone only the fetched commits are scanned.

### Check the documentation

```bash
bash scripts/ci/check-doc-links.sh README.md docs src/Modules/Auth/README.md src/BuildingBlocks
npx --yes markdownlint-cli2 "docs/**/*.md" README.md "src/**/README.md"
bash scripts/ci/check-config-keys.sh
```

Expected output: the link check prints a checked-files summary and exits 0; markdownlint ends with `Summary: 0 issues in 0 files`; the config key check exits 0. Common errors: a backticked repository path that does not exist or a broken relative link (the script prints file and line); a fenced code block without a language (MD040).

<!-- <sample:orders> -->

While the Orders sample exists, also pass its README to the link check: `bash scripts/ci/check-doc-links.sh src/Modules/Orders/README.md`.

<!-- </sample:orders> -->
