using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.BuildingBlocks.Paging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Orders.Jobs;

/// <summary>
/// Recurring job (<see cref="CronExpression"/>, every 15 minutes) that moves pending orders placed longer ago than
/// <c>Orders:PendingExpiry</c> to Expired, in batches of <see cref="BatchSize"/>; each batch runs in its own scope and
/// unit of work, so a long run never holds one big transaction.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Several replicas (or an overlapping slow run) may start it at the same time, so the run is guarded by the
/// distributed lock <see cref="LockName"/>; a run that cannot take it at once is skipped.</item>
/// <item>A batch that fails (for example an order changed concurrently, 409 on its version) is rolled back and ends the
/// run; the next run picks the orders up again. Expiring is idempotent: only pending orders are selected.</item>
/// </list>
/// </remarks>
internal sealed partial class ExpireStalePendingOrdersJob(
    IDistributedLock distributedLock,
    IServiceScopeFactory scopeFactory,
    IOptions<OrdersOptions> options,
    TimeProvider timeProvider,
    ILogger<ExpireStalePendingOrdersJob> logger) : IRecurringJob
{
    /// <summary>Recurring job id.</summary>
    public const string RecurringJobId = "orders-expire-stale-pending";

    /// <summary>Schedule: every 15 minutes (UTC).</summary>
    public const string CronExpression = "*/15 * * * *";

    /// <summary>Distributed lock that keeps runs from overlapping across replicas.</summary>
    public const string LockName = "orders:expire-stale";

    /// <summary>Orders expired per unit of work.</summary>
    public const int BatchSize = 100;

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    /// <summary>Runs once; returns the number of orders expired, or null when another run holds the lock.</summary>
    internal async Task<int?> RunAsync(CancellationToken cancellationToken)
    {
        var handle = await distributedLock.TryAcquireAsync(LockName, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            LogSkipped(logger);
            return null;
        }

        await using (handle.ConfigureAwait(false))
        {
            var now = timeProvider.GetUtcNow();
            var cutoff = now - options.Value.PendingExpiry;
            var total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = await ExpireBatchAsync(cutoff, now, cancellationToken).ConfigureAwait(false);
                if (batch.IsFailure)
                {
                    LogBatchFailed(logger, batch.Error.Code, total);
                    break;
                }

                total += batch.Value;
                if (batch.Value < BatchSize)
                {
                    break;
                }
            }

            LogCompleted(logger, total, cutoff);
            return total;
        }
    }

    private async Task<Result<int>> ExpireBatchAsync(DateTimeOffset cutoff, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredKeyedService<IUnitOfWork>(OrdersModuleKey.Value);

            var selected = 0;
            var result = await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    var stale = new Specification<Order>()
                        .Where(o => o.Status == OrderStatus.Pending && o.CreatedAt < cutoff)
                        .AddOrderBy(o => o.CreatedAt)
                        .ApplyPaging(new PageRequest(1, BatchSize));
                    var batch = await orders.ListAsync(stale, token).ConfigureAwait(false);
                    foreach (var order in batch)
                    {
                        var expired = order.Expire(now);
                        if (expired.IsFailure)
                        {
                            return expired;
                        }
                    }

                    selected = batch.Count;
                    return Result.Success();
                },
                retryOnTransientFailure: false,
                cancellationToken).ConfigureAwait(false);

            return result.IsSuccess ? selected : Result.Failure<int>(result.Error);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stale pending order expiry skipped: another run holds the lock")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stale pending order expiry stopped after {Expired} order(s): a batch failed with {ErrorCode}")]
    private static partial void LogBatchFailed(ILogger logger, string errorCode, int expired);

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Expired} pending order(s) placed before {Cutoff}")]
    private static partial void LogCompleted(ILogger logger, int expired, DateTimeOffset cutoff);
}
