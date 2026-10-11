# Acme.Service

> Applies to: Acme.Service template

A .NET 10 modular-monolith service template. One deployable (`src/Host/Acme.Service.Host/`) hosts independent modules that own their own PostgreSQL schema, talk to each other only through `*.Contracts` assemblies and transactional integration events, and reach infrastructure only through provider-neutral ports.

What you get out of the box:

- **Modules with enforced boundaries.** Vertical-slice use cases, a hand-rolled use-case pipeline (logging, validation, transaction), schema-per-module EF Core persistence, and architecture tests that fail the build when a boundary is crossed (`tests/Acme.Service.ArchitectureTests/`).
- **A self-contained Auth module.** ASP.NET Core Identity, RS256 access tokens from a rotating key ring, single-use refresh tokens with reuse detection, TOTP MFA, RBAC with permissions in the token, and an optional external OIDC login. Authorization fails closed.
- **Reliable messaging.** A transactional outbox per module, idempotent inbox consumers, a dead-letter state, and background jobs.
- **Swappable infrastructure.** Cache (in-memory, Redis), blob storage (local disk, S3-compatible, Azure Blob), email (console, SMTP), jobs (in-memory, Hangfire on PostgreSQL) and distributed locks (in-memory, PostgreSQL advisory locks), each selected by one configuration value.
- **Operations.** RFC 7807 errors everywhere, Serilog and OpenTelemetry, liveness and readiness probes, a `migrate` verb that runs as a separate job, a non-root container image, Docker Compose for local work, Kustomize manifests and GitHub Actions CI.

<!-- <sample:orders> -->

The Orders module (`src/Modules/Orders/`) is a removable sample that demonstrates every pattern end to end: CRUD with ownership rules, paging and sorting, an outbox event, cross-module consumers, a locked recurring job, blob attachments and idempotent creation.

<!-- </sample:orders> -->

## Quick start

Prerequisites: the .NET SDK pinned in `global.json`, and Docker. From the repository root:

```bash
dotnet tool restore
dotnet build Acme.Service.slnx -c Release
dotnet run scripts/dev-env.cs
docker compose -f deploy/compose/compose.yaml up -d --build
curl -s -o /dev/null -w '%{http_code}\n' localhost:8080/health/ready
```

1. Restores the pinned local tools (`.config/dotnet-tools.json`, for example `dotnet-ef`).
2. Builds the solution with warnings treated as errors.
3. Writes `deploy/compose/.env` (gitignored) with a generated JWT signing key and random local passwords.
4. Starts PostgreSQL, Redis, an S3-compatible store, Azurite, Mailpit, the one-shot `migrate` service and the app.
5. Prints `200` once the database is migrated and the app is ready.

To start your own service from this template, run `dotnet run scripts/init.cs -- --name Contoso.Billing` on a fresh clone (add `--remove-sample` to drop the Orders sample, `--dry-run` to preview).

## Documentation

- [Documentation index](docs/README.md)
- [Getting started](docs/guides/getting-started.md)
- [Command cookbook](docs/cookbook.md)
- [Configuration reference](docs/guides/configuration.md)
- [Auth guide](docs/guides/auth.md)
- [Adding a module](docs/guides/adding-a-module.md)
- [Deployment](docs/operations/deployment.md) and [troubleshooting](docs/operations/troubleshooting.md)
- [Architecture overview](docs/architecture/overview.md)
- [Request lifecycle](docs/architecture/request-lifecycle.md)
- [Failure modes](docs/architecture/failure-modes.md)
- [Architecture decision records](docs/adr/README.md)
- [Kubernetes manifests](deploy/k8s/README.md)

## Repository layout

| Path | Contents |
| --- | --- |
| `src/Host/Acme.Service.Host/` | Composition root: middleware, provider selection, module list, `migrate` and `auth:grant-role` verbs |
| `src/BuildingBlocks/` | Ports, `Result`, use-case pipeline, web glue, EF Core base, outbox and inbox |
| `src/BuildingBlocks/Infra/` | One project per infrastructure provider |
| `src/Modules/` | Business modules, each a module project plus a `*.Contracts` project |
| `tests/` | Unit, integration, architecture and shared test fixtures |
| `deploy/` | Docker Compose stack and Kustomize manifests |
| `scripts/` | `init.cs`, `dev-env.cs` and CI helper scripts |
| `docs/` | Architecture, decisions and guides |
