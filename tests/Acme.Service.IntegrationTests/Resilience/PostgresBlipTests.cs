// <sample:orders>
using System.Net;
using Acme.Service.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Acme.Service.IntegrationTests.Resilience;

/// <summary>
/// A Postgres blip (container paused, then unpaused) under D-28 / INV-26: a query is retried and succeeds once the
/// database is back; a non-retryable command answers 503 with Retry-After (or succeeds), and the client's retry with the
/// same Idempotency-Key never creates a duplicate row. Each test owns its container, because pausing it is destructive.
/// </summary>
public sealed class PostgresBlipTests
{
    private const string OrdersPath = "/api/v1/orders";

    private static readonly object _order = new { currency = "EUR", lines = new[] { new { sku = "SKU-1", quantity = 1, unitPrice = 3m } } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListOrders_PostgresPausedThenUnpaused_RetriesAndSucceeds()
    {
        using var logs = new RecordingLoggerProvider();
        await using var host = await PausablePostgresHost.StartAsync(configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(_ =>
                LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Information).AddProvider(logs)))));
        var customer = await host.Api.UserAsync($"blip-query-{Guid.NewGuid():N}@example.com");
        using (var created = await host.Api.PostAsync(OrdersPath, _order, customer.AccessToken))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        await host.PauseAsync();
        var list = host.Api.GetAsync(OrdersPath, customer.AccessToken);

        // Unpause only once the first attempt has failed and EF's retrying strategy has scheduled a retry.
        await ServiceApi.EventuallyAsync(
            () => Task.FromResult(logs.Messages.Any(m => m.Contains("will be retried", StringComparison.Ordinal))),
            "the query is being retried",
            TimeSpan.FromSeconds(30));
        list.IsCompleted.ShouldBeFalse();
        await host.UnpauseAsync();
        using var response = await list.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await ServiceApi.JsonAsync(response)).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateOrder_PostgresPaused_Returns503OrSucceedsAndRetryNeverDuplicates()
    {
        await using var host = await PausablePostgresHost.StartAsync();
        var customer = await host.Api.UserAsync($"blip-command-{Guid.NewGuid():N}@example.com");
        var key = ("Idempotency-Key", Guid.NewGuid().ToString("N"));

        await host.PauseAsync();
        using var duringOutage = await host.Api.PostAsync(OrdersPath, _order, customer.AccessToken, key);
        await host.UnpauseAsync();

        duringOutage.StatusCode.ShouldBeOneOf(HttpStatusCode.ServiceUnavailable, HttpStatusCode.Created);
        if (duringOutage.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            duringOutage.Headers.RetryAfter?.Delta.ShouldNotBeNull();
            (await ServiceApi.ProblemCodeAsync(duringOutage)).ShouldBe("database_unavailable");
        }

        // The client retries with the same key until it gets the order; retrying once more replays it.
        using var retried = await ServiceApi.UntilStatusAsync(
            () => host.Api.PostAsync(OrdersPath, _order, customer.AccessToken, key),
            HttpStatusCode.Created,
            "the retried command succeeds");
        using var replayed = await host.Api.PostAsync(OrdersPath, _order, customer.AccessToken, key);

        replayed.StatusCode.ShouldBe(HttpStatusCode.Created);
        var orderId = (await ServiceApi.JsonAsync(retried)).GetProperty("id").GetGuid();
        (await ServiceApi.JsonAsync(replayed)).GetProperty("id").GetGuid().ShouldBe(orderId);
        (await host.Api.ScalarAsync<long>("SELECT count(*) FROM orders.orders WHERE customer_id = @id", ("id", customer.Id))).ShouldBe(1);
        (await host.Api.ScalarAsync<long>("SELECT count(*) FROM orders.order_lines")).ShouldBe(1);
    }
}

// </sample:orders>
