<!-- <sample:orders> -->

# Orders module (SAMPLE)

> Applies to: Acme.Service template

**This module is a sample.** It exists to show every pattern of the template working end to end, and it is meant to be deleted: `dotnet run scripts/init.cs -- --name <Org.Service> --remove-sample` removes it together with every marked passage elsewhere ([removing the sample](../../../docs/guides/removing-the-sample.md)). Do not build on it; copy what you need into your own module.

Schema `orders`. Routes under `/api/v1/orders`.

## Patterns and where to find them

| Pattern | File |
| --- | --- |
| The only public type, `IModule` wiring | `src/Modules/Orders/Acme.Service.Modules.Orders/OrdersModule.cs` |
| Module key shared by use cases without persistence types | `src/Modules/Orders/Acme.Service.Modules.Orders/OrdersModuleKey.cs` |
| Ports-only use cases: a repository port in the domain, EF Core only in `Persistence/` (INV-04) | `src/Modules/Orders/Acme.Service.Modules.Orders/Domain/IOrderRepository.cs`, `src/Modules/Orders/Acme.Service.Modules.Orders/Persistence/Repositories/OrderRepository.cs` |
| Context, design-time factory and persistence registration | `src/Modules/Orders/Acme.Service.Modules.Orders/Persistence/OrdersDbContext.cs`, `src/Modules/Orders/Acme.Service.Modules.Orders/Persistence/OrdersDesignTimeFactory.cs`, `src/Modules/Orders/Acme.Service.Modules.Orders/Persistence/OrdersPersistence.cs` |
| Aggregate with state transitions and optimistic concurrency (`xmin`) | `src/Modules/Orders/Acme.Service.Modules.Orders/Domain/Order.cs` |
| Permissions and an integration event in Contracts | `src/Modules/Orders/Acme.Service.Modules.Orders.Contracts/OrdersPermissions.cs`, `src/Modules/Orders/Acme.Service.Modules.Orders.Contracts/IntegrationEvents/OrderPlacedV1.cs` |
| Command that writes the order and its event in one transaction (INV-08), with `.WithIdempotency()` | `src/Modules/Orders/Acme.Service.Modules.Orders/Features/CreateOrder/` |
| Paged, sorted, filtered list with a sort whitelist | `src/Modules/Orders/Acme.Service.Modules.Orders/Features/ListOrders/` |
| Permission plus ownership, 404 for records the caller may not see | `src/Modules/Orders/Acme.Service.Modules.Orders/Features/GetOrder/`, `src/Modules/Orders/Acme.Service.Modules.Orders/Features/Shared/OrdersHttp.cs` |
| Inbox consumer of the module's own event, handing off to a job | `src/Modules/Orders/Acme.Service.Modules.Orders/IntegrationEventHandlers/OrderPlacedHandler.cs` |
| Inbox consumer of another module's event, maintaining a local snapshot (INV-06) | `src/Modules/Orders/Acme.Service.Modules.Orders/IntegrationEventHandlers/UserRegisteredHandler.cs` |
| Job calling another module through its Contracts (`IUserDirectory`) and sending email outside any transaction (INV-10) | `src/Modules/Orders/Acme.Service.Modules.Orders/Jobs/SendOrderPlacedNotificationJob.cs` |
| Recurring job guarded by a distributed lock, working in batches | `src/Modules/Orders/Acme.Service.Modules.Orders/Jobs/ExpireStalePendingOrdersJob.cs`, `src/Modules/Orders/Acme.Service.Modules.Orders/Jobs/OrdersJobScheduler.cs` |
| Blob-first upload with compensation, content sniffing, size limit | `src/Modules/Orders/Acme.Service.Modules.Orders/Features/UploadAttachment/` |
| Download via a provider URL (302) or streaming | `src/Modules/Orders/Acme.Service.Modules.Orders/Features/DownloadAttachment/` |
| Validated module options | `src/Modules/Orders/Acme.Service.Modules.Orders/Options/OrdersOptions.cs` |

## Domain

An `Order` belongs to a customer (the caller's `sub`), has 1 to 100 lines (`Sku`, `Quantity` > 0, `UnitPrice` >= 0, `numeric(18,2)`), an ISO 4217 currency and a computed total. Statuses: `Pending`, `Confirmed`, `Cancelled`, `Expired`. Allowed transitions are Pending to Confirmed (manage), Pending to Cancelled (owner with `orders.cancel`, or manage) and Pending to Expired (the recurring job); anything else is 409 `invalid_transition`. Deleting is a soft delete; deleted orders are excluded by a query filter.

## Endpoints

| Method and route | Permission | Notes |
| --- | --- | --- |
| `POST /api/v1/orders` | `orders.create` | 201 with `Location`. Accepts `Idempotency-Key`: a retry replays the first response; the same key with a different body is 422. |
| `GET /api/v1/orders` | `orders.read` | Own orders, or all with `orders.manage`. Filters `status`, `createdFrom`, `createdTo`; sort `createdAt`, `total`, `status`; paging `page`, `pageSize`. |
| `GET /api/v1/orders/{id}` | `orders.read` | 404 for another customer's order unless the caller has `orders.manage`. |
| `PUT /api/v1/orders/{id}` | `orders.create` (own) or `orders.manage` | Replaces the lines of a pending order; the body carries the `version` read earlier, and a stale version is 409 `concurrency_conflict`. |
| `POST /api/v1/orders/{id}/confirm` | `orders.manage` | |
| `POST /api/v1/orders/{id}/cancel` | `orders.cancel` (own) or `orders.manage` | |
| `DELETE /api/v1/orders/{id}` | `orders.manage` | Soft delete, 204. |
| `POST /api/v1/orders/{id}/attachments` | `orders.create` (own) or `orders.manage` | Multipart; PDF, PNG or JPEG detected from the content, not the header; at most `Orders:Attachments:MaxBytes` (10 MiB, else 413). |
| `GET /api/v1/orders/{id}/attachments/{attachmentId}` | `orders.read` (own) or `orders.manage` | 302 to a short-lived provider URL when the blob provider supports it, otherwise the file is streamed. |

Error codes are listed in the [error model](../../../docs/architecture/error-model.md).

## Data (schema `orders`)

`orders`, `order_lines`, `order_attachments`, `customer_snapshots`, plus `outbox_messages`, `inbox_messages` and the migration history. The list query is backed by indexes on `(customer_id, created_at desc)` and `status`. Attachment blobs are stored under `orders/{orderId}/{attachmentId}`.

## Messaging and jobs

- `CreateOrder` enqueues `orders.order-placed.v1`. `OrderPlacedHandler` (consumer `orders.send-order-placed-notification`) enqueues `SendOrderPlacedNotificationJob`, which looks up the customer through `IUserDirectory` and emails them; a deleted user is skipped.
- `UserRegisteredHandler` consumes Auth's `auth.user-registered.v1` and upserts `customer_snapshots`, ignoring events older than the stored row.
- `ExpireStalePendingOrdersJob` (recurring id `orders-expire-stale-pending`, every 15 minutes, lock `orders:expire-stale`) moves pending orders older than `Orders:PendingExpiry` (24 hours) to `Expired` in batches of 100.

## Configuration

`Orders:PendingExpiry` (default `1.00:00:00`) and `Orders:Attachments:MaxBytes` (default `10485760`); see [configuration](../../../docs/guides/configuration.md).

## Known limitations

- Orphan blobs are possible when both the metadata save and the compensating delete fail (or the process crashes in between); there is no sweep job.
- The staff order list does not yet show the customer email, although the snapshot is maintained.
- A retried notification job can send the email twice.

## Tests

`tests/Acme.Service.Modules.Orders.Tests/`: domain rules, endpoints with Customer, Staff and Admin tokens, list paging and sorting, concurrency, idempotency, attachments, messaging and the expiry job.

<!-- </sample:orders> -->
