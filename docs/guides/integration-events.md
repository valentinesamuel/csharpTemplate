# Integration events

> Applies to: Acme.Service template

Integration events tell other modules (or the producing module itself, later and outside the transaction) that something happened. They are written to the producer's outbox in the same transaction as the change and delivered at least once to idempotent consumers through each consumer's inbox. The mechanics are described in [messaging](../architecture/messaging.md) and [ADR-0004](../adr/0004-transactional-outbox-and-inbox.md); this guide is the how-to.

## When to use an event

- Another module needs to react to a change (send an email, update a local snapshot, start a process).
- Your own module must do external I/O after a command (email, HTTP call): the command enqueues an event, a handler enqueues a job, and the job does the I/O outside any transaction (INV-10).

Use a Contracts query interface instead when the caller needs an answer now (for example `IUserDirectory`). Never call another module's use case or write to its schema.

## 1. Declare the event

Public events live in the producer's Contracts project, one file per event and version:

```csharp
// src/Modules/Invoices/Acme.Service.Modules.Invoices.Contracts/IntegrationEvents/InvoiceIssuedV1.cs
using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.Modules.Invoices.Contracts.IntegrationEvents;

/// <summary>An invoice was issued.</summary>
[IntegrationEvent("invoices.invoice-issued.v1")]
public sealed record InvoiceIssuedV1(Guid Id, DateTimeOffset OccurredAt, Guid InvoiceId, Guid CustomerId) : IIntegrationEvent;
```

- `Id` is a Guid v7 and becomes the outbox row id and the inbox de-duplication key; `OccurredAt` is UTC.
- The name in `[IntegrationEvent]` is what is persisted, never the CLR type name (INV-19). Format: `<module>.<kebab-name>.v<n>`, lower case, `n` starting at 1, unique across the service. The architecture test R7 enforces it.
- Carry ids and the facts consumers need, not whole aggregates and never secrets or tokens (the payload is stored as `jsonb`).
- Events used only inside the producing module (like Auth's email-verification request) can stay `internal` in the module project; register them with `AddIntegrationEventTypes(typeof(MyEvent))`.

## 2. Register the event types

Every module that produces or consumes events adds the Contracts assemblies that declare them to the type registry, in `AddServices`:

```csharp
services.AddIntegrationEvents(typeof(InvoiceIssuedV1).Assembly, typeof(UserRegisteredV1).Assembly);
```

Adding the same assembly from several modules is harmless. A duplicate event name fails at startup. A row whose type name is not registered is marked dead immediately by the dispatcher.

## 3. Publish from a command

```csharp
internal sealed class IssueInvoiceUseCase(
    IRepository<Invoice, Guid> invoices,
    [FromKeyedServices(InvoicesModuleKey.Value)] IOutbox outbox,
    TimeProvider timeProvider) : IUseCase<IssueInvoiceCommand, InvoiceResponse>
{
    public async Task<Result<InvoiceResponse>> ExecuteAsync(IssueInvoiceCommand input, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var invoice = Invoice.Issue(input.CustomerId, now);
        await invoices.AddAsync(invoice, cancellationToken);
        outbox.Enqueue(new InvoiceIssuedV1(Guid.CreateVersion7(now), now, invoice.Id, invoice.CustomerId));
        return InvoiceResponse.From(invoice);
    }
}
```

- Inject the outbox keyed by your module name; it writes into your schema's `outbox_messages`.
- Enqueue only from an `ICommand` use case, so the transaction behaviour commits the business change and the event together (INV-08). If the command fails, neither exists.
- Never publish to `IEventBus` or enqueue a job for another module directly from a use case.

<!-- <sample:orders> -->

Reference: `src/Modules/Orders/Acme.Service.Modules.Orders/Features/CreateOrder/CreateOrderUseCase.cs` enqueues `OrderPlacedV1`.

<!-- </sample:orders> -->

## 4. Consume

```csharp
internal sealed class InvoiceIssuedHandler(IJobQueue jobs) : IIntegrationEventHandler<InvoiceIssuedV1>
{
    public const string ConsumerName = "invoices.send-invoice-email";

    public Task HandleAsync(InvoiceIssuedV1 integrationEvent, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync<SendInvoiceEmailJob, InvoiceEmailPayload>(new InvoiceEmailPayload(integrationEvent.InvoiceId), cancellationToken);
}

// in the consuming module's AddServices
services.AddIntegrationEventHandler<InvoiceIssuedV1, InvoiceIssuedHandler>(Name, InvoiceIssuedHandler.ConsumerName);
```

- `AddIntegrationEventHandler` is the only supported registration. It wraps the handler in the inbox decorator, which runs it inside the consuming module's unit of work and records `(message_id, consumer)` in that module's `inbox_messages`; a redelivered message is skipped (INV-09). A handler registered any other way is never called.
- The consuming module must have its own `AddModuleDbContext` (the inbox lives in its schema).
- Always pass an explicit, stable consumer name. The default is `<module>.<HandlerClassName>`, so renaming the class would make already-processed messages look new.
- Handlers must be idempotent beyond the inbox: delivery order is not guaranteed. When maintaining a snapshot, compare the event's `OccurredAt` (or a version) with what is stored and ignore older events.
- No external I/O in the handler: enqueue a job ([background jobs](background-jobs.md)) with ids in the payload.
- Throwing makes the dispatcher retry the message with exponential backoff (2^attempts seconds, at most 5 minutes); other consumers of the same message that already succeeded are skipped by their own inbox rows.

<!-- <sample:orders> -->

References: `src/Modules/Orders/Acme.Service.Modules.Orders/IntegrationEventHandlers/OrderPlacedHandler.cs` (own event to a job) and `src/Modules/Orders/Acme.Service.Modules.Orders/IntegrationEventHandlers/UserRegisteredHandler.cs` (Auth's event to a local snapshot, ignoring stale events).

<!-- </sample:orders> -->

## Delivery guarantees

- At least once, in-process, typically within `Outbox:PollingInterval` (2 seconds) of the commit.
- Each module's dispatcher runs on every replica; `FOR UPDATE SKIP LOCKED` keeps replicas from taking the same rows.
- After `Outbox:MaxAttempts` (10) failures a message is dead: it stays in `outbox_messages` with `dead_at` set, an Error is logged and the `outbox.dead` counter increments. Replay is a SQL update ([troubleshooting](../operations/troubleshooting.md#dead-outbox-messages)).
- Processed outbox rows are deleted after `Outbox:RetentionDays` (7), inbox rows after `Inbox:RetentionDays` (30), by the hourly `outbox-cleanup` job.

## Versioning

- A published event is additive-only within its version: add optional properties, never remove, rename or retype one.
- A breaking change is a new type and name (`InvoiceIssuedV2`, `invoices.invoice-issued.v2`). Publish both while consumers migrate, then stop publishing V1 once no consumer needs it and no V1 rows are pending.
- Do not rename the CLR type of an event without keeping the attribute name: the attribute is the contract.

## Testing

- Assert that the command and the outbox row commit or roll back together.
- Deliver the same event twice and assert a single effect.
- For snapshots, deliver an older event after a newer one and assert it is ignored.

`tests/Acme.Service.BuildingBlocks.Tests/Messaging/OutboxInboxTests.cs` covers the mechanics; the module tests show end-to-end checks.
