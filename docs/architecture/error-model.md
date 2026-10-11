# Error model

> Applies to: Acme.Service template

Every non-2xx response is an RFC 7807 problem document with content type `application/problem+json`. Success bodies are wrapped as `{ isSuccess, requestId, correlationId, data }`; problems stay flat and carry the same `isSuccess` and `requestId` ([ADR-0017](../adr/0017-response-envelope.md), INV-18; supersedes ADR-0011).

## Shape

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "The order is no longer pending.",
  "code": "invalid_transition",
  "isSuccess": false,
  "requestId": "0HNP7R1GJ7MSQ00000002",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
  "correlationId": "checkout-7f3a"
}
```

| Member | Source |
| --- | --- |
| `status`, `title`, `type` | ASP.NET Core ProblemDetails defaults for the status code. |
| `detail` | The `Error.Message` of a failed `Result`. Removed for unhandled exceptions outside Development. |
| `code` | Machine-readable, stable, snake_case. From `Error.Code`, or derived from the status when the response did not come from a `Result` (for example `unauthorized`, `not_found`, `internal_error`). |
| `isSuccess` | Always `false` on a problem. |
| `requestId` | Alphanumeric per-request id; the same value as the `requestId` log field and the `X-Request-Id` header. |
| `traceId` | The W3C trace id of the current activity. |
| `correlationId` | The request's correlation id (also in the `X-Correlation-Id` response header). |
| `errors` | Validation failures only: a dictionary from property name to messages. |

Success example: `{ "isSuccess": true, "requestId": "0HNP7R1GJ7MSQ00000002", "correlationId": "checkout7f3a", "data": { ... } }`. 204, redirects and streams have no body; the ids are in the `X-Request-Id` and `X-Correlation-Id` headers.

Clients should branch on `status` and `code`, never on `title` or `detail`.

## Where problems come from

| Origin | Mechanism |
| --- | --- |
| A use case returns `Result.Failure(error)` | `ToHttpResult()` in `src/BuildingBlocks/Acme.Service.BuildingBlocks.Web/Results/ResultHttpExtensions.cs` maps `ErrorType` to a status and writes the problem with `code`. |
| Validation fails in the pipeline | `ValidationBehavior` returns `validation_failed`, rendered as a validation problem with `errors`. |
| An exception escapes | `UseExceptionHandler()` renders 500 `internal_error`. Outside Development the message and stack trace are removed (`src/BuildingBlocks/Acme.Service.BuildingBlocks.Web/Errors/ProblemDetailsSetup.cs`). |
| Middleware sets an error status without a body (404 unmatched route with a token, 401 challenge, 403 policy failure, 405, 415) | `UseStatusCodePages()` renders a problem with the status-derived `code`. |
| Rate limiter rejection | 429 `too_many_requests` with `Retry-After`. |
| `live-session` policy cannot reach the session store | 503 `session_store_unavailable` with `Retry-After`, written by `src/Modules/Auth/Acme.Service.Modules.Auth/Sessions/LiveSessionResultHandler.cs`. |

## ErrorType to status

| `ErrorType` | Status | Notes |
| --- | --- | --- |
| `Validation` | 400 | Validation problem with `errors`. |
| `Unauthorized` | 401 | |
| `Forbidden` | 403 | |
| `NotFound` | 404 | Also used instead of 403 when revealing existence would leak data (another customer's resource). |
| `Conflict` | 409 | Concurrency conflicts, unique violations, invalid state transitions. |
| `Unprocessable` | 422 | Semantically invalid request, for example an idempotency key reused with a different body. |
| `TooManyRequests` | 429 | |
| `Unavailable` | 503 | Always with `Retry-After: 5`. The client may retry, with the same `Idempotency-Key` for commands. |

## Error codes

Status-derived codes: `bad_request`, `unauthorized`, `forbidden`, `not_found`, `method_not_allowed`, `not_acceptable`, `conflict`, `payload_too_large`, `unsupported_media_type`, `unprocessable`, `too_many_requests`, `internal_error`, `unavailable`.

Shared codes from the building blocks:

| Code | Status | Meaning |
| --- | --- | --- |
| `validation_failed` | 400 | One or more input fields are invalid; see `errors`. |
| `invalid_paging` | 400 | `page` or `pageSize` is not a valid number. |
| `invalid_sort` | 400 | A `sort` field is not in the endpoint's whitelist. |
| `invalid_idempotency_key` | 400 | `Idempotency-Key` is longer than 128 characters or uses characters outside `[A-Za-z0-9-_]`. |
| `idempotency_in_progress` | 409 | A request with the same key is still running. |
| `idempotency_key_reused` | 422 | The key was used before with a different body. |
| `idempotency_unavailable` | 503 | The cache backing idempotency is down and `Idempotency:FailMode=Closed`. |
| `concurrency_conflict` | 409 | The row changed since it was read (optimistic concurrency). |
| `unique_violation` | 409 | A unique constraint was violated (PostgreSQL 23505). |
| `database_unavailable` | 503 | A transient database fault in a command that is not retryable. |

Auth codes:

| Code | Status | Meaning |
| --- | --- | --- |
| `invalid_credentials` | 401 | Wrong email or password, unknown user, or locked account. Deliberately indistinguishable. |
| `email_not_verified` | 403 | Correct password, but the email is not confirmed. |
| `invalid_token` | 400 | Email confirmation or password reset token is invalid or expired. |
| `invalid_refresh_token` | 401 | Refresh token unknown, used, expired or revoked. |
| `session_revoked` | 401 | The token's session has been revoked (`live-session` endpoints). |
| `session_store_unavailable` | 503 | The session store could not be checked; the request is denied. |
| `invalid_mfa_token`, `invalid_mfa_code` | 401 | The MFA challenge token, or the TOTP or recovery code presented with it, was rejected at login. |
| `invalid_code` | 400 | The TOTP code presented to enable MFA is wrong. |
| `invalid_password` | 400 | Password re-confirmation failed (MFA disable, new recovery codes, change password). |
| `mfa_already_enabled`, `mfa_not_enabled` | 409 | MFA state does not allow the operation. |
| `user_not_found`, `role_not_found` | 404 | Administration target does not exist. |
| `last_admin` | 409 | Removing the role would leave no unlocked Admin. |
| `invalid_return_url`, `external_login_failed` | 400 | External OIDC login: `returnUrl` not on the allowlist, or the provider callback failed. |
| `invalid_exchange_code` | 401 | The one-time external login code is unknown, used or expired. |
| `external_provider_unavailable` | 503 | The external identity provider could not be reached. |

<!-- <sample:orders> -->

Orders sample codes:

| Code | Status | Meaning |
| --- | --- | --- |
| `order_not_found` | 404 | No such order, or it belongs to another customer and the caller lacks `orders.manage`. |
| `invalid_transition` | 409 | The order status does not allow the operation. |
| `invalid_filter` | 400 | A list filter is malformed. |
| `attachment_not_found` | 404 | No such attachment on the order. |
| `unsupported_attachment_type`, `invalid_attachment` | 400 | The file is not a PDF, PNG or JPEG by content, or the upload is malformed. |
| `attachment_too_large` | 413 | The file exceeds `Orders:Attachments:MaxBytes`. |

<!-- </sample:orders> -->

The exact status of codes listed with alternatives depends on the endpoint; the OpenAPI document (`/openapi/v1.json` in Development) lists the problem responses per operation.

## Lists

List endpoints return:

```json
{ "items": [], "page": 1, "pageSize": 20, "totalCount": 0, "totalPages": 0 }
```

`page` is 1-based; `pageSize` defaults to 20 and is capped at 100; `sort` is a comma-separated, per-endpoint whitelist such as `sort=-createdAt,total`.

## Rules for new code

- Return `Result.Failure(Error.X("snake_code", "message"))` for expected failures; throw only for bugs and infrastructure faults.
- Never put secrets, tokens, SQL or stack traces in `detail`.
- Add every new code to this page and to the endpoint's OpenAPI metadata (`ProducesProblem`).
