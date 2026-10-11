// <sample:orders>
using System.Net;
using Acme.Service.IntegrationTests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.IntegrationTests.Resilience;

/// <summary>
/// The Redis cache goes away mid-test (container stopped). Reads keep working; an Idempotency-Key request is refused
/// with 503 (FailMode Closed, INV-26) rather than run without protection; authentication never depends on Redis
/// (INV-13, D-09, D-12): login, refresh and the <c>live-session</c> endpoint logout-all (Auth database only) still work.
/// The Redis container belongs to this test alone, because stopping it is destructive.
/// </summary>
public sealed class RedisDownTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RedisStopped_ReadsAuthAndLiveSessionKeepWorking_IdempotentWritesFailClosed()
    {
        await using var redis = new RedisFixture();
        await redis.InitializeAsync();
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await ServiceApi.MigrateAsync(connectionString);
        await using var factory = new IntegrationWebFactory(connectionString, new Dictionary<string, string?>
        {
            ["Cache:Provider"] = "Redis",
            ["Cache:Redis:ConnectionString"] = redis.ConnectionString,
            ["Idempotency:FailMode"] = "Closed",
        });
        var api = new ServiceApi(factory);
        var email = $"redis-down-{Guid.NewGuid():N}@example.com";
        var customer = await api.UserAsync(email);
        var order = new { currency = "EUR", lines = new[] { new { sku = "SKU-1", quantity = 1, unitPrice = 5m } } };

        // While Redis is up, idempotency is enforced through it: the retry replays the first order.
        var firstKey = ("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using (var created = await api.PostAsync("/api/v1/orders", order, customer.AccessToken, firstKey))
        using (var replayed = await api.PostAsync("/api/v1/orders", order, customer.AccessToken, firstKey))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            replayed.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await ServiceApi.JsonAsync(replayed)).GetProperty("id").GetGuid()
                .ShouldBe((await ServiceApi.JsonAsync(created)).GetProperty("id").GetGuid());
        }

        await redis.StopAsync(Ct);

        using var list = await api.GetAsync("/api/v1/orders", customer.AccessToken);
        using var idempotent = await api.PostAsync("/api/v1/orders", order, customer.AccessToken, ("Idempotency-Key", Guid.NewGuid().ToString("N")));
        using var login = await api.LoginAsync(email);
        using var refresh = await api.RefreshAsync(customer.RefreshToken);
        var refreshed = await ServiceApi.TokensOfAsync(refresh);
        using var logoutAll = await api.PostAsync("/api/v1/auth/logout-all", null, refreshed.AccessToken);

        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ServiceApi.JsonAsync(list)).GetProperty("totalCount").GetInt32().ShouldBe(1);
        idempotent.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ServiceApi.ProblemCodeAsync(idempotent)).ShouldBe("idempotency_unavailable");
        (await api.ScalarAsync<long>("SELECT count(*) FROM orders.orders WHERE customer_id = @id", ("id", customer.Id))).ShouldBe(1);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        logoutAll.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await api.ScalarAsync<long>(
            "SELECT count(*) FROM auth.sessions WHERE user_id = @id AND revoked_at IS NULL",
            ("id", customer.Id))).ShouldBe(0);
    }
}

// </sample:orders>
