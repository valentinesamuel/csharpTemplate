# ADR-0011: ProblemDetails for errors, no response envelope

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Clients need one predictable error format across modules, middleware and unhandled exceptions, with enough information to correlate a failure with logs and traces, and without leaking internals. A common alternative is to wrap every response, success or failure, in an envelope such as `{ data, error, meta }`.

## Decision

Every non-2xx response is RFC 7807 `application/problem+json` with three extensions: `code` (stable, machine-readable), `traceId` and `correlationId`. Success responses are plain DTOs with the appropriate status (200, 201 with `Location`, 202, 204); lists use `{ items, page, pageSize, totalCount, totalPages }`. There is no envelope. Use cases return `Result`/`Error` with an `ErrorType`; `ToHttpResult()` maps it to a status (`src/BuildingBlocks/Acme.Service.BuildingBlocks.Web/Results/ResultHttpExtensions.cs`). The exception handler and status-code pages render the same shape (`src/BuildingBlocks/Acme.Service.BuildingBlocks.Web/Errors/ProblemDetailsSetup.cs`). Details: [error model](../architecture/error-model.md).

## Options considered

### Option A: ProblemDetails for errors, plain bodies for success (chosen)

- Pros: an IETF standard supported by ASP.NET Core, OpenAPI tooling and many client libraries; HTTP status codes keep their meaning; success payloads are simple to consume and document.
- Cons: clients must look at the status before parsing the body; two body shapes instead of one.

### Option B: a uniform envelope for every response

- Pros: one parsing path on clients.
- Cons: duplicates the HTTP status in the body; tends to drift into 200-with-error responses; non-standard; every endpoint and test pays the wrapping cost.

### Option C: framework defaults only

- Pros: no code.
- Cons: no stable `code`, no correlation id, inconsistent shapes between middleware and endpoints, and exception details in some paths.

## Consequences

- Clients branch on `status` and `code`; `detail` is human-readable only.
- 503 responses from `ErrorType.Unavailable` always include `Retry-After`.
- Outside Development, unhandled exceptions produce 500 `internal_error` with no message or stack trace.
- Validation errors include an `errors` dictionary.
- Every new error code is documented in the error model and declared in the endpoint's OpenAPI metadata.

## Related invariants

- INV-18: uniform error model.
- INV-28: no secrets or internal details in responses or logs.
