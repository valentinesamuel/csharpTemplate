using System.Collections.Concurrent;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.Inbox;
using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Outbox;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.BuildingBlocks.Tests.Messaging.Fixtures;

/// <summary>Records handler invocations and injects handler failures.</summary>
internal sealed class HandlerProbe
{
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failuresRemaining = new(StringComparer.Ordinal);

    public ConcurrentBag<string?> CorrelationIds { get; } = [];

    public int Calls(string handler) => _calls.GetValueOrDefault(handler);

    public void FailNext(string handler, int times) => _failuresRemaining[handler] = times;

    /// <summary>Counts a call, then throws while injected failures remain for <paramref name="handler"/>.</summary>
    public void Record(string handler)
    {
        _calls.AddOrUpdate(handler, 1, (_, count) => count + 1);

        while (_failuresRemaining.TryGetValue(handler, out var remaining) && remaining > 0)
        {
            if (_failuresRemaining.TryUpdate(handler, remaining - 1, remaining))
            {
                throw new InvalidOperationException($"Simulated failure of {handler}.");
            }
        }
    }
}

/// <summary>Adds <see cref="WidgetOrderedV1.Amount"/> to the quantity of the named widget (the side effect under test).</summary>
internal sealed class IncrementWidgetHandler(WidgetsDbContext context, HandlerProbe probe, ICorrelationContext correlation)
    : IIntegrationEventHandler<WidgetOrderedV1>
{
    public const string Name = nameof(IncrementWidgetHandler);

    public async Task HandleAsync(WidgetOrderedV1 integrationEvent, CancellationToken cancellationToken)
    {
        probe.CorrelationIds.Add(correlation.CorrelationId);
        probe.Record(Name);
        var widget = await context.Widgets.SingleAsync(w => w.Name == integrationEvent.WidgetName, cancellationToken);
        widget.Quantity += integrationEvent.Amount;
    }
}

/// <summary>A second consumer of the same event that writes its own side effect (a new widget row).</summary>
internal sealed class AuditWidgetHandler(WidgetsDbContext context, HandlerProbe probe) : IIntegrationEventHandler<WidgetOrderedV1>
{
    public const string Name = nameof(AuditWidgetHandler);

    public async Task HandleAsync(WidgetOrderedV1 integrationEvent, CancellationToken cancellationToken)
    {
        probe.Record(Name);
        await context.Widgets.AddAsync(new Widget { Name = $"audit-{integrationEvent.Id:N}" }, cancellationToken);
    }
}

/// <summary>AsyncLocal correlation context, as the Host bridge provides in the web app.</summary>
internal sealed class TestCorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> _current = new();

    public string? CorrelationId
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

/// <summary>Counts deliveries per message id instead of invoking consumers (isolates the dispatcher's own guarantees).</summary>
internal sealed class CountingEventBus(ConcurrentDictionary<Guid, int> deliveries) : IEventBus
{
    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        deliveries.AddOrUpdate(integrationEvent.Id, 1, (_, count) => count + 1);
        await Task.Delay(2, cancellationToken);
    }
}

/// <summary>Fails the dispatcher's mark-processed save (after the consumers committed), simulating a crash.</summary>
internal sealed class CrashBeforeMarkProcessedInterceptor : SaveChangesInterceptor
{
    private int _crashesRemaining;

    public int CrashesRemaining
    {
        get => Volatile.Read(ref _crashesRemaining);
        set => Volatile.Write(ref _crashesRemaining, value);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var marksProcessed = eventData.Context?.ChangeTracker.Entries<OutboxMessage>()
            .Any(e => e.State == EntityState.Modified && e.Entity.ProcessedAt is not null) ?? false;
        if (marksProcessed && Interlocked.Decrement(ref _crashesRemaining) >= 0)
        {
            throw new InvalidOperationException("Simulated crash before the outbox row was marked processed.");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>The widgets module wired as both producer and consumer of <see cref="WidgetOrderedV1"/>.</summary>
internal static class MessagingHost
{
    public static Task<WidgetsHost> CreateAsync(
        Testing.Containers.PostgresFixture postgres,
        CancellationToken cancellationToken,
        Action<IServiceCollection>? configure = null) =>
        WidgetsHost.CreateAsync(postgres, cancellationToken, services =>
        {
            services.AddIntegrationEvents(typeof(WidgetOrderedV1).Assembly);
            services.AddSingleton<HandlerProbe>();
            services.AddSingleton<ICorrelationContext, TestCorrelationContext>();
            configure?.Invoke(services);
        });

    public static void SubscribeIncrement(IServiceCollection services) =>
        services.AddIntegrationEventHandler<WidgetOrderedV1, IncrementWidgetHandler>(WidgetsDbContext.ModuleKey);

    public static void SubscribeAudit(IServiceCollection services) =>
        services.AddIntegrationEventHandler<WidgetOrderedV1, AuditWidgetHandler>(WidgetsDbContext.ModuleKey);

    public static OutboxDispatcher<WidgetsDbContext> Dispatcher(this WidgetsHost host) =>
        host.Services.GetServices<IHostedService>().OfType<OutboxDispatcher<WidgetsDbContext>>().Single();

    public static HandlerProbe Probe(this WidgetsHost host) => host.Services.GetRequiredService<HandlerProbe>();

    /// <summary>Runs <paramref name="operation"/> in the module unit of work with access to the keyed outbox.</summary>
    public static async Task<Result> InTransactionAsync(
        this WidgetsHost host,
        Func<WidgetsDbContext, IOutbox, CancellationToken, Task<Result>> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutbox>(WidgetsDbContext.ModuleKey);
        var unitOfWork = scope.ServiceProvider.GetRequiredKeyedService<IUnitOfWork>(WidgetsDbContext.ModuleKey);
        return await unitOfWork.ExecuteInTransactionAsync(ct => operation(context, outbox, ct), retryOnTransientFailure: false, cancellationToken);
    }

    public static async Task EnqueueAsync(this WidgetsHost host, CancellationToken cancellationToken, params IIntegrationEvent[] events)
    {
        var result = await host.InTransactionAsync(
            (_, outbox, _) =>
            {
                foreach (var integrationEvent in events)
                {
                    outbox.Enqueue(integrationEvent);
                }

                return Task.FromResult(Result.Success());
            },
            cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Code);
        }
    }

    public static async Task<List<OutboxMessage>> OutboxAsync(this WidgetsHost host, CancellationToken cancellationToken)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();
        return await context.Set<OutboxMessage>().AsNoTracking().OrderBy(m => m.Id).ToListAsync(cancellationToken);
    }

    public static async Task<List<InboxMessage>> InboxAsync(this WidgetsHost host, CancellationToken cancellationToken)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();
        return await context.Set<InboxMessage>().AsNoTracking().OrderBy(m => m.Consumer).ToListAsync(cancellationToken);
    }

    public static async Task<int> QuantityAsync(this WidgetsHost host, string widgetName, CancellationToken cancellationToken) =>
        (await host.ReadAllAsync(cancellationToken)).Single(w => w.Name == widgetName).Quantity;
}
