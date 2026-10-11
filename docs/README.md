# Documentation

> Applies to: Acme.Service template

Start with the [architecture overview](architecture/overview.md), then read the documents that match what you are changing. Decisions and their trade-offs are recorded as ADRs; each architecture document links to the ADRs it depends on.

## Architecture

| Document | Read it when you need to know |
| --- | --- |
| [Overview](architecture/overview.md) | What the parts are and how they fit together |
| [Module boundaries](architecture/module-boundaries.md) | What a module may reference, and how the rules are enforced |
| [Request lifecycle](architecture/request-lifecycle.md) | The middleware order and the use-case pipeline, step by step |
| [Data ownership](architecture/data-ownership.md) | Schemas, why there are no cross-schema joins, snapshots vs Contracts calls |
| [Messaging](architecture/messaging.md) | Outbox, inbox, at-least-once delivery, idempotent consumers, dead letters |
| [Error model](architecture/error-model.md) | The ProblemDetails shape, status mapping and error codes |
| [Security model](architecture/security-model.md) | Fail-closed authorization, tokens, sessions, MFA, secrets |
| [Observability](architecture/observability.md) | Logs, traces, metrics, correlation ids and health probes |
| [Failure modes](architecture/failure-modes.md) | What happens when a dependency or input misbehaves |

## Guides

| Guide | Use it to |
| --- | --- |
| [Getting started](guides/getting-started.md) | Build, run locally or in Docker Compose, make a first call, start your own service |
| [Configuration](guides/configuration.md) | Look up any setting: key, environment variable, default, whether it is a secret |
| [Adding a module](guides/adding-a-module.md) | Create a module with Contracts, schema, migrations, permissions and tests |
| [Adding a use case](guides/adding-a-use-case.md) | Add a command or query with validation, an endpoint and paging |
| [Adding a provider](guides/adding-a-provider.md) | Point a provider at a new service (Cloudflare R2) or add a new provider |
| [Integration events](guides/integration-events.md) | Publish and consume events through the outbox and inbox |
| [Background jobs](guides/background-jobs.md) | Write one-off and recurring jobs; Hangfire specifics |
| [Auth](guides/auth.md) | Every authentication flow with `curl`, MFA, external OIDC, admin bootstrap |
| [Permissions](guides/permissions.md) | Declare, seed and check permissions |
| [Testing](guides/testing.md) | Run and write unit, integration and architecture tests |
| [Command cookbook](cookbook.md) | Copy-paste commands, each verified: packages, migrations, tests, Docker, init, auth operations, Kubernetes and CI checks |

<!-- <sample:orders> -->

| Guide | Use it to |
| --- | --- |
| [Removing the sample](guides/removing-the-sample.md) | Delete the Orders sample with the init script or by hand |

<!-- </sample:orders> -->

## Operations

| Runbook | Use it to |
| --- | --- |
| [Deployment](operations/deployment.md) | Deploy to Kubernetes in the right order; expand/contract; rollback |
| [Migrations](operations/migrations.md) | Create, review and apply schema changes |
| [Key rotation](operations/key-rotation.md) | Rotate the JWT signing key without downtime, or in an emergency |
| [Secrets](operations/secrets.md) | Know what is secret and which Secret holds it |
| [Troubleshooting](operations/troubleshooting.md) | Readiness 503, Redis degraded, dead outbox messages, the Hangfire dashboard, 401/403/503 |
| [Security hardening](operations/security-hardening.md) | Encrypt Data Protection keys, database roles, forwarded headers, cookie-based refresh |

## Code-level READMEs

- [BuildingBlocks](../src/BuildingBlocks/README.md) and [infrastructure providers](../src/BuildingBlocks/Infra/README.md)
- [Auth module](../src/Modules/Auth/README.md)

<!-- <sample:orders> -->

- [Orders sample module](../src/Modules/Orders/README.md)

<!-- </sample:orders> -->

## Decisions

The [ADR index](adr/README.md) lists every architecture decision record. New decisions start from the [ADR template](adr/0000-template.md).

## Conventions for these documents

- Every document states which template it applies to on its second line.
- Paths in backticks and relative links must exist; `bash scripts/ci/check-doc-links.sh README.md docs src/Modules/Auth/README.md src/BuildingBlocks` fails when they do not (without arguments it checks `README.md` and `docs/` only).

<!-- <sample:orders> -->

- While the sample exists, also pass its README to the link check: `bash scripts/ci/check-doc-links.sh src/Modules/Orders/README.md`.

<!-- </sample:orders> -->

- Style is checked with `npx --yes markdownlint-cli2 "docs/**/*.md" README.md "src/**/README.md"` using `.markdownlint.jsonc`.
- Every configuration key must appear in the [configuration reference](guides/configuration.md); `bash scripts/ci/check-config-keys.sh` fails otherwise.
- Content about the Orders sample sits between `<!-- <sample:orders> -->` and `<!-- </sample:orders> -->` lines so that `scripts/init.cs --remove-sample` can delete it.
- Mermaid diagrams are allowed.
