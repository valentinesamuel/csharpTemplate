# ADR-0017: Response envelope with `isSuccess` and `requestId`

> Applies to: Acme.Service template

- **Status:** Accepted (supersedes [ADR-0011](0011-problem-details-no-envelope.md))
- **Date:** 2026-10-11

## Context

ADR-0011 returned bare DTOs on success. Clients could not tell a success from a failure without reading the status, and neither a success nor an error carried the per-request id that the logs call `requestId`. Support could not match a client response to a log line, and the Kestrel request id (`0HNP7R1GJ7MSQ:00000002`) contains a `:`.

## Decision

- Every success response that has a body is `{ "isSuccess": true, "requestId", "correlationId", "data": <DTO> }` (`EnvelopeResult.Wrap` in `src/BuildingBlocks/Acme.Service.BuildingBlocks.Web/Results/ApiEnvelope.cs`, applied by `ResultHttpExtensions.ToHttpResult`). The status code and `Location` header are unchanged. 204, redirects and file/stream responses have no body and are not wrapped.
- Every non-2xx response stays RFC 7807 `application/problem+json`, flat, with `isSuccess: false` and `requestId` added to `code`, `traceId` and `correlationId`.
- `requestId` is `HttpContext.TraceIdentifier` reduced to `[A-Za-z0-9]` by `CorrelationIdMiddleware` (`0HNP7R1GJ7MSQ00000002`). Logs, the body and the `X-Request-Id` response header carry the same value, including on 204.
- All ids are alphanumeric: an inbound `X-Correlation-Id` must match `[A-Za-z0-9]{1,64}`, otherwise it is replaced by a generated id. Generated ids and `traceId` are 32 hex characters.

## Options considered

### Option A: envelope on every response (chosen)

- Pros: one parsing path; `isSuccess` and the id are visible to every client; support can quote one id.
- Cons: breaking change for existing clients; the HTTP status is partly duplicated in the body.

### Option B: ProblemDetails extensions only (errors only)

- Pros: smallest change. Cons: success responses still show nothing.

### Option C: header only

- Pros: no body change. Cons: `isSuccess` is not in the body.

## Consequences

- Clients read `data` on success. Idempotent replays return the stored body, so `requestId` in a replay is the id of the original request.
- Inbound correlation ids with `-` or `_` (for example hyphenated UUIDs) are no longer accepted.
- Lists keep their paging shape, inside `data`.

## Related invariants

- INV-18: uniform error model. INV-28: no secrets in responses or logs.
