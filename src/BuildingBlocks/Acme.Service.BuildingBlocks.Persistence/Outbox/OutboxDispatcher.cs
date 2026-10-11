using System.Diagnostics;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Acme.Service.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Polls one module's <c>outbox_messages</c> and publishes each message to the in-process
/// <see cref="IEventBus"/> (D-05). One instance per module, registered by <c>AddModuleDbContext</c>. Safe with
/// any number of replicas: each batch is locked with <c>FOR UPDATE SKIP LOCKED</c> inside one transaction.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Open a transaction and lock up to <see cref="OutboxOptions.BatchSize"/> due messages in id (Guid v7) order.</item>
/// <item>Per message (its own try/catch, so a poison message never blocks the batch): resolve the type by its
/// stable name (unknown type or unreadable payload: dead at once), deserialize, publish to every consumer.</item>
/// <item>Success sets <c>processed_at</c>. Failure increments <c>attempts</c> and sets <c>next_attempt_at</c> to
/// now + 2^attempts s (capped at 5 min); at <see cref="OutboxOptions.MaxAttempts"/> it sets <c>dead_at</c>,
/// logs an error and increments <c>outbox.dead</c>.</item>
/// <item>Commit. A crash before the commit redelivers the batch; the inbox makes that harmless (INV-09).</item>
/// </list>
/// "Now" comes from <see cref="TimeProvider"/> and is passed to the query as a parameter. When the database
/// is unavailable or the schema is missing the loop backs off (up to 1 minute) and logs a warning at most
/// once per minute. Payloads are never logged (INV-28).
/// </remarks>
/// <typeparam name="TContext">The producing module's context.</typeparam>
internal sealed partial class OutboxDispatcher<TContext> : BackgroundService
    where TContext : ModuleDbContext
{
    /// <summary>Upper bound of the delay between retries of a failed message.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound of the delay between polls while the database is unavailable.</summary>
    public static readonly TimeSpan MaxUnavailableDelay = TimeSpan.FromMinutes(1);

    /// <summary>Minimum interval between two "database unavailable" warnings.</summary>
    public static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    private const int MaxErrorLength = 2000;

    private readonly string _moduleKey;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly IntegrationEventTypeRegistry _registry;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxMetrics _metrics;
    private readonly ILogger _logger;
    private readonly ICorrelationContext? _correlation;

    public OutboxDispatcher(
        string moduleKey,
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        IntegrationEventTypeRegistry registry,
        IOptions<OutboxOptions> options,
        TimeProvider timeProvider,
        OutboxMetrics metrics,
        ILogger<OutboxDispatcher<TContext>> logger,
        ICorrelationContext? correlation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ArgumentNullException.ThrowIfNull(options);

        _moduleKey = moduleKey;
        _scopeFactory = scopeFactory;
        _eventBus = eventBus;
        _registry = registry;
        _options = options.Value;
        _timeProvider = timeProvider;
        _metrics = metrics;
        _logger = logger;
        _correlation = correlation;
    }

    /// <summary>The delay before retrying a message that has failed <paramref name="attempts"/> times: 2^attempts s, capped at 5 min.</summary>
    public static TimeSpan ComputeBackoff(int attempts)
    {
        var exponent = Math.Clamp(attempts, 1, 30);
        var delay = TimeSpan.FromSeconds(1L << exponent);
        return delay < MaxBackoff ? delay : MaxBackoff;
    }

    /// <summary>
    /// Locks and dispatches one batch in one transaction. Returns the number of messages locked. Throws when the
    /// database cannot be reached or the batch cannot be committed (the messages are then redelivered).
    /// </summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var messages = await context.Set<OutboxMessage>()
                    .FromSqlRaw(
                        context.MessagingSql().SelectOutboxBatch,
                        new NpgsqlParameter("now", _timeProvider.GetUtcNow()),
                        new NpgsqlParameter("batch", _options.BatchSize))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var message in messages)
                {
                    try
                    {
                        await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Shutting down: keep what was completed; the rest stays pending.
                        break;
                    }
                }

                await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return messages.Count;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var failureDelay = TimeSpan.Zero;
        DateTimeOffset? lastWarningAt = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var locked = await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
                failureDelay = TimeSpan.Zero;
                if (locked >= _options.BatchSize)
                {
                    continue;
                }

                delay = _options.PollingInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failureDelay = failureDelay == TimeSpan.Zero
                    ? _options.PollingInterval
                    : TimeSpan.FromTicks(Math.Min(failureDelay.Ticks * 2, MaxUnavailableDelay.Ticks));

                var now = _timeProvider.GetUtcNow();
                if (lastWarningAt is null || now - lastWarningAt.Value >= WarningInterval)
                {
                    lastWarningAt = now;
                    LogUnavailable(_logger, ex, _moduleKey, failureDelay);
                }

                delay = failureDelay;
            }

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (!_registry.TryGetType(message.Type, out var eventType))
        {
            MarkDead(message, "Unknown integration event type; it is not in the type registry.");
            return;
        }

        IIntegrationEvent integrationEvent;
        try
        {
            integrationEvent = IntegrationEventSerializer.Deserialize(eventType, message.Payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A payload that cannot be read now will not become readable by retrying (JsonException,
            // NotSupportedException, or a constructor rejecting the data): poison, so dead at once.
            // Never record the exception message: it may quote payload content (INV-28).
            MarkDead(message, $"The payload could not be deserialized ({ex.GetType().Name}).");
            return;
        }

        try
        {
            await PublishAsync(message, integrationEvent, cancellationToken).ConfigureAwait(false);
            message.ProcessedAt = _timeProvider.GetUtcNow();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            message.Attempts++;
            message.LastError = Describe(ex);
            if (message.Attempts >= _options.MaxAttempts)
            {
                message.DeadAt = _timeProvider.GetUtcNow();
                LogDead(_logger, ex, _moduleKey, message.Id, message.Type, message.Attempts);
                _metrics.RecordDead(_moduleKey, message.Type);
                return;
            }

            var backoff = ComputeBackoff(message.Attempts);
            message.NextAttemptAt = _timeProvider.GetUtcNow() + backoff;
            LogRetry(_logger, ex, _moduleKey, message.Id, message.Type, message.Attempts, backoff);
        }
    }

    private async Task PublishAsync(OutboxMessage message, IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        // Restore the enqueuing operation's correlation id and trace for the consumers (D-25). The async
        // method boundary scopes the AsyncLocal change; the finally makes the restore explicit.
        var previousCorrelationId = _correlation?.CorrelationId;
        if (_correlation is not null)
        {
            _correlation.CorrelationId = message.CorrelationId;
        }

        _ = ActivityContext.TryParse(message.Traceparent, traceState: null, out var parent);
        using var activity = MessagingTelemetry.ActivitySource.StartActivity(
            MessagingTelemetry.DispatchActivityName, ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.message.id", message.Id);
        activity?.SetTag("messaging.destination.name", message.Type);
        activity?.SetTag("module", _moduleKey);

        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = message.CorrelationId,
            ["MessageId"] = message.Id,
            ["EventType"] = message.Type,
        });

        try
        {
            await _eventBus.PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_correlation is not null)
            {
                _correlation.CorrelationId = previousCorrelationId;
            }
        }
    }

    private void MarkDead(OutboxMessage message, string error)
    {
        message.LastError = error;
        message.DeadAt = _timeProvider.GetUtcNow();
        LogUndeliverable(_logger, _moduleKey, message.Id, message.Type, error);
        _metrics.RecordDead(_moduleKey, message.Type);
    }

    private static string Describe(Exception exception)
    {
        var failures = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception];
        var text = string.Join("; ", failures.Select(e => $"{e.GetType().Name}: {e.Message}"));
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventType}) of module {Module} failed (attempt {Attempts}); retrying in {Backoff}")]
    private static partial void LogRetry(ILogger logger, Exception exception, string module, Guid messageId, string eventType, int attempts, TimeSpan backoff);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({EventType}) of module {Module} is dead after {Attempts} attempts")]
    private static partial void LogDead(ILogger logger, Exception exception, string module, Guid messageId, string eventType, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({EventType}) of module {Module} is dead: {Reason}")]
    private static partial void LogUndeliverable(ILogger logger, string module, Guid messageId, string eventType, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox dispatcher of module {Module} cannot reach its outbox (database unavailable or schema missing); retrying in {Delay}")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, string module, TimeSpan delay);
}
