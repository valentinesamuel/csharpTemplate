# Adding a use case

> Applies to: Acme.Service template

A use case is one business operation: an input record, a validator, the use case class and, usually, an HTTP endpoint. It runs through the shared pipeline (logging, validation, transaction) whether it is called from an endpoint, a job or an event handler. How the pipeline works is in [request lifecycle](../architecture/request-lifecycle.md) and [ADR-0002](../adr/0002-hand-rolled-use-case-pipeline.md).

## Layout

One folder per use case under the module's `Features/`:

```text
src/Modules/Invoices/Acme.Service.Modules.Invoices/Features/IssueInvoice/
  IssueInvoiceCommand.cs     input record (+ request and response DTOs)
  IssueInvoiceValidator.cs   FluentValidation rules
  IssueInvoiceUseCase.cs     IUseCase<IssueInvoiceCommand, InvoiceResponse>
  IssueInvoiceEndpoint.cs    IEndpoint (minimal API)
```

Everything is `internal sealed` (INV-02, R8). Use cases, validators and endpoints are discovered from the module assembly; there is nothing to register.

## 1. The input: command or query

```csharp
internal sealed record IssueInvoiceCommand(Guid CustomerId, string? Currency, decimal? Amount) : ICommand<InvoiceResponse>;

internal sealed record IssueInvoiceRequest(string? Currency, decimal? Amount);   // the HTTP body
```

- `ICommand<TOut>` changes state. The pipeline runs it inside the module's unit of work: a successful result saves and commits; a failure result or an exception rolls back.
- `IQuery<TOut>` only reads. No transaction; reads outside a transaction retry transient database faults.
- A command that is safe to run twice can also implement `IRetryableCommand`; only then is its transaction retried on transient faults. Any other command answers a transient fault with 503 and `Retry-After`, and the client retries, ideally with an `Idempotency-Key` (D-28, INV-26).
- Take the caller's identity (`sub`) from the endpoint and pass it in the input; never trust an id from the body for ownership.

## 2. The validator

```csharp
internal sealed class IssueInvoiceValidator : AbstractValidator<IssueInvoiceCommand>
{
    public IssueInvoiceValidator()
    {
        RuleFor(c => c.CustomerId).NotEmpty();
        RuleFor(c => c.Currency).NotEmpty().Length(3);
        RuleFor(c => c.Amount).NotNull().GreaterThan(0);
    }
}
```

The validation behaviour runs every `IValidator<TIn>` before the use case; failures become 400 `validation_failed` with field errors. Validators are pure: no database calls (check existence in the use case and return `NotFound` or `Conflict`). Built-in minimal-API validation is deliberately not used, so rules apply equally to jobs and consumers.

## 3. The use case

```csharp
internal sealed class IssueInvoiceUseCase(
    IRepository<Invoice, Guid> invoices,
    [FromKeyedServices(InvoicesModuleKey.Value)] IOutbox outbox,
    TimeProvider timeProvider) : IUseCase<IssueInvoiceCommand, InvoiceResponse>
{
    public async Task<Result<InvoiceResponse>> ExecuteAsync(IssueInvoiceCommand input, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (/* a business rule fails */ false)
        {
            return Error.Conflict("invoice_already_issued", "The invoice has already been issued.");
        }

        var invoice = Invoice.Issue(input.CustomerId, input.Currency!, input.Amount!.Value, now);
        await invoices.AddAsync(invoice, cancellationToken);
        outbox.Enqueue(new InvoiceIssuedV1(Guid.CreateVersion7(now), now, invoice.Id));
        return InvoiceResponse.From(invoice);
    }
}
```

- Return `Result<T>`: a value converts implicitly to success, an `Error` to failure. `Error.Validation`, `Unauthorized`, `Forbidden`, `NotFound`, `Conflict`, `Unprocessable`, `TooManyRequests` and `Unavailable` map to 400, 401, 403, 404, 409, 422, 429 and 503. Use a stable lower-case `code`; it is part of the API ([error model](../architecture/error-model.md)).
- Depend on ports only: `IRepository<,>` or your module's repository interface, the module-keyed `IUnitOfWork` and `IOutbox`, `ICache`, `IBlobStore`, `IJobQueue`, `IDistributedLock`, other modules' Contracts. No EF Core types outside `Persistence/` (INV-04).
- Inject `TimeProvider`; create ids with `Guid.CreateVersion7()`.
- No external I/O (email, HTTP, blob writes) inside a command: enqueue an event or a job instead (INV-10). The one documented exception is a blob upload written before the transaction opens, with a compensating delete if the save fails; such a use case is not an `ICommand` and opens its own unit of work after the blob write.
- Inject the keyed `IUnitOfWork` and call `SaveChangesAsync` only when you need database-generated values (such as a concurrency token) before building the response; otherwise the pipeline saves on success.
- Exceptions are not converted into results. Return errors for expected outcomes; let unexpected exceptions reach the exception handler (500 `internal_error`).

## 4. The endpoint

```csharp
internal sealed class IssueInvoiceEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/invoices", async (IssueInvoiceRequest body, HttpContext context,
                IUseCaseExecutor<IssueInvoiceCommand, InvoiceResponse> executor) =>
            {
                if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId))
                {
                    return Results.Unauthorized();
                }

                var result = await executor.ExecuteAsync(new IssueInvoiceCommand(userId, body.Currency, body.Amount), context.RequestAborted);
                return result.ToHttpResult(invoice => TypedResults.Created($"/api/v1/invoices/{invoice.Id}", invoice));
            })
            .RequirePermission(InvoicesPermissions.Issue)
            .WithIdempotency()
            .WithName("IssueInvoice")
            .WithTags("Invoices")
            .Produces<InvoiceResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
```

- Routes are `/api/v1/<resource-plural>` in kebab-case; JSON is camelCase with enums as strings.
- Always call the use case through `IUseCaseExecutor<TIn,TOut>`, never the use case class directly, or validation and the transaction are skipped.
- `ToHttpResult()` maps a failure to a problem document with `code`, `traceId` and `correlationId`; pass a success mapper for 201 and similar.
- Protect the endpoint with `.RequirePermission(...)` ([permissions](permissions.md)). Add `.RequireAuthorization("live-session")` for sensitive operations. Anonymous endpoints need `.AllowAnonymous()` and an entry in `tests/Acme.Service.IntegrationTests/Security/AnonymousEndpointAllowlist.cs`.
- `.WithIdempotency()` (requires `AddIdempotency` in the Host, already present) lets clients retry a POST safely with an `Idempotency-Key` header.
- OpenAPI metadata (`WithName`, `WithSummary`, `Produces...`) keeps `/openapi/v1.json` accurate.

## Lists, paging and sorting

```csharp
internal static class ListInvoicesSort
{
    public static readonly SortMap<Invoice> Map = new SortMap<Invoice>()
        .Add("createdAt", i => i.CreatedAt)
        .Add("total", i => i.Total);
}

// in the endpoint
var list = context.Request.BindListQuery(ListInvoicesSort.Map);
if (list.IsFailure)
{
    return list.ToHttpResult();      // 400 for a bad page, page size or unknown sort field
}
```

`BindListQuery` reads `page` (1-based), `pageSize` (default 20, at most 100) and `sort` (for example `sort=-createdAt,total`, only whitelisted fields). Build a `Specification<T>` with the criteria, sort and paging, call `ListAsync` and `CountAsync`, and return a `PagedResult<T>`, which serialises as `{ items, page, pageSize, totalCount, totalPages }`. Lists of other users' records filter by owner unless the caller holds the managing permission.

## Calling a use case from a job or a handler

Inject `IUseCaseExecutor<TIn,TOut>` in the job or handler and call `ExecuteAsync`; the same validation and transaction rules apply. Inside an integration event handler the inbox already opened the module's transaction, and a nested unit of work joins it.

## Tests

- Unit-test the validator and the domain rules directly.
- Integration-test the endpoint through a `ServiceWebFactory` subclass with a real PostgreSQL database: success, validation failure (400), missing token (401), missing permission (403), not found or not owned (404) and conflicts (409). See [testing](testing.md).
