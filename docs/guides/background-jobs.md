# Background jobs

> Applies to: Acme.Service template

Jobs run work outside the request and outside any database transaction: sending email, calling external APIs, periodic clean-up. Modules use the provider-neutral `IJobQueue` port; `Jobs:Provider` selects the implementation ([ADR-0010](../adr/0010-hangfire-with-postgres-storage.md)).

| Provider | Use it for | Durability | Retries |
| --- | --- | --- | --- |
| `InMemory` | Development and tests only (refused in `Production`) | Lost on restart; per process | 3 retries 1 second apart (4 attempts), then logged as failed and dropped |
| `Hangfire` | Everything else | Persisted in the `hangfire` schema of the service database | Hangfire's default policy (10 attempts with increasing delays), then the Failed state |

## The port

```csharp
Task<string> EnqueueAsync<TJob, TPayload>(TPayload payload, CancellationToken cancellationToken) where TJob : IJob<TPayload>;
Task<string> ScheduleAsync<TJob, TPayload>(TPayload payload, TimeSpan delay, CancellationToken cancellationToken) where TJob : IJob<TPayload>;
Task AddOrUpdateRecurringAsync<TJob>(string recurringJobId, string cronExpression, CancellationToken cancellationToken) where TJob : IRecurringJob;
```

Defined in `src/BuildingBlocks/Acme.Service.BuildingBlocks/Jobs/IJobQueue.cs`, with `IJob<TPayload>` and `IRecurringJob` beside it.

## A one-off job

```csharp
internal sealed record InvoiceEmailPayload(Guid InvoiceId);

internal sealed class SendInvoiceEmailJob(
    IRepository<Invoice, Guid> invoices,
    IUserDirectory users,
    INotificationSender email) : IJob<InvoiceEmailPayload>
{
    public async Task ExecuteAsync(InvoiceEmailPayload payload, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetByIdAsync(payload.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return; // nothing to do: completing is better than retrying forever
        }

        var user = await users.GetUserSummaryAsync(invoice.CustomerId, cancellationToken);
        if (user is null)
        {
            return;
        }

        await email.SendEmailAsync(new EmailMessage(user.Email, "Your invoice", "..."), cancellationToken);
    }
}

// in AddServices
services.TryAddScoped<SendInvoiceEmailJob>();
```

- Enqueue from an integration event handler or another job, not from a command: a job enqueued inside a command would run even if the transaction rolled back (and with Hangfire it is stored through a different connection). The reliable path is command, then outbox event, then handler, then job ([integration events](integration-events.md)).
- The payload is serialised with System.Text.Json. Put ids in it and load current data in the job; never put secrets, tokens or email bodies in a payload. Auth generates confirmation and reset tokens inside the job for this reason.
- Each run gets its own DI scope, and the correlation id that was current when the job was enqueued is restored, so logs join the original request.
- Jobs run at least once. Make them idempotent, or accept a rare duplicate (an email sent twice).
- Registering the job class in DI is recommended (`TryAddScoped<TJob>()`); the providers resolve it from the scope, or create it with constructor injection if it is not registered.
- Throw to retry. Return normally when retrying cannot help (the record is gone), and log why.

## A recurring job

```csharp
internal sealed class PurgeDraftsJob(IDistributedLock locks /* , ... */) : IRecurringJob
{
    public const string RecurringJobId = "invoices-purge-drafts";
    public const string CronExpression = "0 3 * * *";      // 03:00 UTC daily
    public const string LockName = "invoices:purge-drafts";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await using var handle = await locks.TryAcquireAsync(LockName, TimeSpan.Zero, cancellationToken);
        if (handle is null)
        {
            return; // another replica is already running it
        }

        // work in bounded batches, each in its own unit of work
    }
}
```

Recurring jobs are registered when the web app starts, from a hosted service in the module, which calls `AddOrUpdateRecurringAsync` off the startup path and retries if the job store is not reachable yet (for example before `migrate` has created the Hangfire schema). Copy `src/BuildingBlocks/Acme.Service.BuildingBlocks.Persistence/Outbox/OutboxCleanupScheduler.cs`, which registers the template's own `outbox-cleanup` job every hour, and register it with `services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, YourScheduler>())`.

- Cron expressions have 5 fields, or 6 with leading seconds, and are evaluated in UTC by both providers.
- Every replica registers the same id; providers keep one recurring job per id, and Hangfire enqueues it once per tick across replicas. The in-memory provider runs a schedule per process, which is why a recurring job should still take an `IDistributedLock` with a zero wait, so runs never overlap.
- Keep the recurring job id stable. Changing it leaves the old schedule behind in Hangfire until you delete it.

<!-- <sample:orders> -->

Reference: `src/Modules/Orders/Acme.Service.Modules.Orders/Jobs/ExpireStalePendingOrdersJob.cs` (every 15 minutes, lock `orders:expire-stale`, batches of 100, one unit of work per batch), scheduled by `src/Modules/Orders/Acme.Service.Modules.Orders/Jobs/OrdersJobScheduler.cs`; `src/Modules/Orders/Acme.Service.Modules.Orders/Jobs/SendOrderPlacedNotificationJob.cs` is a one-off job that calls Auth through `IUserDirectory`.

<!-- </sample:orders> -->

## Hangfire specifics

- **Schema.** The web host composition never creates the `hangfire` schema (INV-16). The migrate path installs or upgrades it when `Jobs:Provider=Hangfire` is set for the migrate run (this includes the opt-in `Migrations:RunOnStart` step, which uses the web app's `Jobs__Provider`). If the app starts first, it stays ready, but Hangfire workers log errors and `IJobQueue` calls fail until `migrate` has run.
- **Timing.** Scheduled and recurring jobs are polled every 15 seconds, so they can start up to about 15 seconds late, never early.
- **Workers.** `Jobs:Hangfire:WorkerCount` per replica (default: processor count x 5, at most 20). Every replica runs a Hangfire server.
- **Renames.** Persisted jobs reference the CLR names of the job and payload types. Renaming or moving either (including running `scripts/init.cs` on a service that already has persisted jobs) orphans the stored jobs. Drain the queues before such a rename.
- **Licence.** Hangfire.Core and Hangfire.PostgreSql are LGPL-3.0 (OQ-04). Because modules see only `IJobQueue`, a Quartz.NET or other provider can replace Hangfire as a new Infra project.

## The Hangfire dashboard (development only)

The dashboard is mapped at `/hangfire` only when all three hold: `Jobs:Provider=Hangfire`, `Jobs:Hangfire:DashboardEnabled=true` and the environment is `Development`. Outside Development the setting is ignored with a warning. It accepts local requests only and does not show the storage connection string. The Compose stack runs as `Production`, so use a local `dotnet run` ([troubleshooting](../operations/troubleshooting.md#the-hangfire-dashboard-in-development)).

## Testing jobs

- Unit-test a job by constructing it with substitutes and calling `ExecuteAsync`.
- The provider behaviour is covered by `tests/Acme.Service.Testing/Contracts/JobQueueContractTests.cs`, run against both providers.
- To test that a job is enqueued, use the in-memory provider (the test host default) and poll with a timeout for the effect; never use fixed sleeps.
