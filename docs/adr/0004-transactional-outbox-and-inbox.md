# ADR-0004: Transactional outbox and inbox

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Modules react to each other's facts (a user registered, an order was placed) without sharing transactions. Publishing an event after commit can lose it on a crash; publishing before commit can announce something that rolls back. The template runs several replicas against one PostgreSQL database and has no message broker in v1.

## Decision

Each module writes integration events to its own `outbox_messages` table through the keyed `IOutbox`, in the same transaction as the business change. A per-module `OutboxDispatcher<TContext>` background service polls with `SELECT ... FOR UPDATE SKIP LOCKED`, publishes in-process through `IEventBus`, retries with exponential backoff and marks messages dead after `Outbox:MaxAttempts`. Every consumer runs through `InboxHandlerDecorator`, which records `(message_id, consumer)` in the consumer's `inbox_messages` inside the consumer's unit of work, so redeliveries are skipped. Events have stable names from `[IntegrationEvent("<module>.<name>.v<n>")]`. Details: [messaging](../architecture/messaging.md).

## Options considered

### Option A: polling outbox with SKIP LOCKED, backoff and a dead state (chosen)

- Pros: atomic with the business change; safe with any number of replicas; no extra infrastructure; poison messages are isolated per row; observable through `outbox.dead`.
- Cons: polling latency (default 2 seconds); load proportional to the polling rate; delivery is at-least-once, so consumers must be idempotent.

### Option B: PostgreSQL LISTEN/NOTIFY

- Pros: lower latency.
- Cons: notifications are lost when no listener is connected, so polling is still needed; more connection management. Can be added later as a latency optimisation on top of A.

### Option C: one Hangfire job per message

- Pros: reuses the job infrastructure and its dashboard.
- Cons: couples messaging to an optional, LGPL-licensed provider; enqueueing the job is itself a second write that must be atomic with the business change.

### Option D: an external broker (RabbitMQ, Kafka, a cloud bus)

- Pros: the standard for cross-service messaging.
- Cons: more infrastructure for an in-process monolith; still needs an outbox for atomicity.

## Consequences

- Use cases never publish directly; they enqueue in the outbox (INV-08).
- Handlers must be registered with `AddIntegrationEventHandler<TEvent,THandler>(moduleKey, consumerName)`; raw handler registrations are ignored so the inbox cannot be bypassed.
- Consumers tolerate duplicates and out-of-order delivery, and do no external I/O inside their transaction; they enqueue jobs instead.
- Dead messages stay for inspection and manual replay; processed rows are deleted after `Outbox:RetentionDays`, inbox rows after `Inbox:RetentionDays`.
- Contracts events are additive-only within a version; a breaking change is a new `V<n+1>` type with a new name.
- Moving to a broker later replaces the dispatcher's publish step, not the use cases.

## Related invariants

- INV-08: outbox atomicity.
- INV-09: idempotent consumers through the inbox.
- INV-10: no external I/O inside a database transaction.
- INV-19: stable event identity.
