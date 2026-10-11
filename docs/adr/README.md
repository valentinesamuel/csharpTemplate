# Architecture decision records

> Applies to: Acme.Service template

Each record captures one decision: the context, the options that were weighed, what was chosen and what it costs. New records start from the [template](0000-template.md).

| ADR | Title | Status |
| --- | --- | --- |
| [0001](0001-modular-monolith.md) | Modular monolith | Accepted |
| [0002](0002-hand-rolled-use-case-pipeline.md) | Hand-rolled use-case pipeline, no MediatR | Accepted |
| [0003](0003-schema-per-module.md) | One PostgreSQL schema per module | Accepted |
| [0004](0004-transactional-outbox-and-inbox.md) | Transactional outbox and inbox | Accepted |
| [0005](0005-auth-module-uses-identity-and-ef.md) | Auth module uses ASP.NET Core Identity and EF Core directly | Accepted |
| [0006](0006-rs256-jwt-key-ring-and-rotation.md) | RS256 access tokens, key ring and rotation | Accepted |
| [0007](0007-refresh-rotation-with-reuse-detection.md) | Refresh token rotation with reuse detection, strictly single-use | Accepted, pending design-review confirmation |
| [0008](0008-provider-model.md) | Provider model for infrastructure | Accepted |
| [0009](0009-migrations-as-a-separate-job.md) | Migrations as a separate job | Accepted |
| [0010](0010-hangfire-with-postgres-storage.md) | Hangfire with PostgreSQL storage | Accepted, licence confirmation open |
| [0011](0011-problem-details-no-envelope.md) | ProblemDetails for errors, no response envelope | Superseded by 0017 |
| [0012](0012-in-process-rate-limiting.md) | In-process rate limiting | Accepted |
| [0013](0013-permissions-in-the-access-token.md) | Permissions in the access token | Accepted |
| [0014](0014-external-idp-via-oidc-with-code-exchange.md) | External identity provider via OIDC with code exchange, no auto-link | Accepted |
| [0015](0015-test-and-licence-choices.md) | Test libraries and licence policy | Accepted |
| [0016](0016-ef-confinement-to-persistence-namespace.md) | EF Core confined to the Persistence namespace | Accepted, pending design-review confirmation |
| [0017](0017-response-envelope.md) | Response envelope with `isSuccess` and `requestId` | Accepted |

Invariant identifiers (`INV-xx`) refer to the system-wide rules that the architecture tests, CI checks and reviews enforce; each architecture document cites the ones it depends on.
