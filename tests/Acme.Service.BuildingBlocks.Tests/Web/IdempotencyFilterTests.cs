using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Tests.Web.Fixtures;
using Acme.Service.BuildingBlocks.Web.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Web;

public sealed class IdempotencyFilterTests
{
    private const string Route = "/things";

    private readonly FakeCache _cache = new();
    private int _executions;
    private Func<int, HttpContext, IResult> _respond = (n, _) => TypedResults.Created($"/things/{n}", new { id = n });
    private TaskCompletionSource? _entered;
    private TaskCompletionSource? _release;

    [Fact]
    public async Task Invoke_NoHeader_ProcessesEveryRequestWithoutCaching()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        (await PostAsync(client, key: null)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostAsync(client, key: null)).StatusCode.ShouldBe(HttpStatusCode.Created);

        _executions.ShouldBe(2);
        _cache.Keys.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("ключ")]
    public async Task Invoke_InvalidKey_Returns400WithoutExecuting(string key)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var response = await PostAsync(client, key);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe("invalid_idempotency_key");
        _executions.ShouldBe(0);
    }

    [Fact]
    public async Task Invoke_KeyLongerThan128_Returns400()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        (await PostAsync(client, new string('a', 129))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(client, new string('a', 128))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Invoke_SameKeyAndBody_ReplaysStatusBodyAndLocation()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var first = await PostAsync(client, "key-1");
        var second = await PostAsync(client, "key-1");

        _executions.ShouldBe(1);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        first.Headers.Contains(IdempotencyFilter.ReplayedHeaderName).ShouldBeFalse();
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.GetValues(IdempotencyFilter.ReplayedHeaderName).ShouldBe(["true"]);
        second.Headers.Location.ShouldBe(first.Headers.Location);
        second.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        (await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invoke_FirstRequest_StoresCompletedEntryUnderScopedKeyWithTtl()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        await PostAsync(client, "key-1", sub: "user-7");

        const string expectedKey = "idem:user-7:POST:/things:key-1";
        _cache.Keys.ShouldBe([expectedKey]);
        _cache.Writes.ShouldContain((expectedKey, TimeSpan.FromMinutes(1)));
        _cache.Writes.ShouldContain((expectedKey, TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task Invoke_Replay_NeverIncludesSetCookie()
    {
        _respond = (n, http) =>
        {
            http.Response.Cookies.Append("session", "value");
            return TypedResults.Created($"/things/{n}", new { id = n });
        };
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var first = await PostAsync(client, "key-1");
        var replay = await PostAsync(client, "key-1");

        first.Headers.Contains("Set-Cookie").ShouldBeTrue();
        replay.Headers.Contains(IdempotencyFilter.ReplayedHeaderName).ShouldBeTrue();
        replay.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task Invoke_ConcurrentSameKey_Returns409InProgressAndExecutesOnce()
    {
        _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var firstTask = PostAsync(client, "key-1");
        await _entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var concurrent = await PostAsync(client, "key-1");
        _release.SetResult();
        var first = await firstTask;

        concurrent.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(concurrent)).ShouldBe("idempotency_in_progress");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        _executions.ShouldBe(1);
    }

    [Fact]
    public async Task Invoke_SameKeyDifferentBody_Returns422KeyReused()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        await PostAsync(client, "key-1", body: """{"name":"a"}""");
        var reused = await PostAsync(client, "key-1", body: """{"name":"b"}""");

        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadCodeAsync(reused)).ShouldBe("idempotency_key_reused");
        _executions.ShouldBe(1);
    }

    [Fact]
    public async Task Invoke_SameKeyDifferentUsers_AreIsolated()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var first = await PostAsync(client, "key-1", sub: "user-1");
        var other = await PostAsync(client, "key-1", sub: "user-2");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        other.StatusCode.ShouldBe(HttpStatusCode.Created);
        other.Headers.Contains(IdempotencyFilter.ReplayedHeaderName).ShouldBeFalse();
        _executions.ShouldBe(2);
    }

    [Fact]
    public async Task Invoke_CacheUnavailableFailClosed_Returns503ProblemWithRetryAfter()
    {
        _cache.IsAvailable = false;
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var response = await PostAsync(client, "key-1");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        response.Headers.RetryAfter.ShouldNotBeNull();
        (await ReadCodeAsync(response)).ShouldBe("idempotency_unavailable");
        _executions.ShouldBe(0);
    }

    [Fact]
    public async Task Invoke_TryAddThrowsFailClosed_Returns503()
    {
        _cache.ThrowOnTryAdd = true;
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var response = await PostAsync(client, "key-1");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ReadCodeAsync(response)).ShouldBe("idempotency_unavailable");
        _executions.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invoke_CacheUnavailableFailOpen_ProcessesRequestWithoutIdempotency(bool throwOnTryAdd)
    {
        _cache.IsAvailable = throwOnTryAdd;
        _cache.ThrowOnTryAdd = throwOnTryAdd;
        await using var app = await StartAsync(failMode: "Open");
        using var client = app.GetTestClient();

        (await PostAsync(client, "key-1")).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostAsync(client, "key-1")).StatusCode.ShouldBe(HttpStatusCode.Created);

        _executions.ShouldBe(2);
    }

    [Fact]
    public async Task Invoke_ServerError_IsNotCachedAndRetryExecutesAgain()
    {
        _respond = (n, _) => n == 1
            ? TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError)
            : TypedResults.Created($"/things/{n}", new { id = n });
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        (await PostAsync(client, "key-1")).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _cache.Keys.ShouldBeEmpty();

        var retry = await PostAsync(client, "key-1");

        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.Contains(IdempotencyFilter.ReplayedHeaderName).ShouldBeFalse();
        _executions.ShouldBe(2);
    }

    [Fact]
    public async Task Invoke_HandlerThrows_RemovesMarkerSoRetryExecutes()
    {
        _respond = (n, _) => n == 1
            ? throw new InvalidOperationException("boom")
            : TypedResults.Created($"/things/{n}", new { id = n });
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        (await PostAsync(client, "key-1")).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _cache.Keys.ShouldBeEmpty();

        (await PostAsync(client, "key-1")).StatusCode.ShouldBe(HttpStatusCode.Created);
        _executions.ShouldBe(2);
    }

    [Theory]
    [InlineData(StatusCodes.Status409Conflict)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    public async Task Invoke_ConflictOrTooManyRequests_IsNotCached(int status)
    {
        _respond = (_, _) => TypedResults.Problem(statusCode: status);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        await PostAsync(client, "key-1");
        await PostAsync(client, "key-1");

        _cache.Keys.ShouldBeEmpty();
        _executions.ShouldBe(2);
    }

    [Fact]
    public async Task Invoke_ClientError_IsCachedAndReplayed()
    {
        _respond = (_, _) => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        await PostAsync(client, "key-1");
        var replay = await PostAsync(client, "key-1");

        replay.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        replay.Headers.Contains(IdempotencyFilter.ReplayedHeaderName).ShouldBeTrue();
        _executions.ShouldBe(1);
    }

    [Fact]
    public async Task Invoke_HandlerReturnsPlainValue_ReplaysJsonBody()
    {
        await using var app = await StartAsync(map: a => a.MapPost("/plain", async (ThingInput input) =>
        {
            await Task.Yield();
            Interlocked.Increment(ref _executions);
            return new ThingInput(input.Name + "!");
        }).WithIdempotency());
        using var client = app.GetTestClient();

        var first = await PostAsync(client, "key-1", path: "/plain");
        var replay = await PostAsync(client, "key-1", path: "/plain");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("""{"name":"a!"}""");
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("""{"name":"a!"}""");
        _executions.ShouldBe(1);
    }

    [Fact]
    public async Task AddIdempotency_InProgressTtlAboveTtl_FailsOnStart()
    {
        var exception = await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(
            () => StartAsync(extraConfig: new() { ["Idempotency:Ttl"] = "00:00:30", ["Idempotency:InProgressTtl"] = "00:01:00" }));

        exception.Message.ShouldContain("Idempotency:InProgressTtl");
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("Order-2026_10_08-abcXYZ", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a.b", false)]
    [InlineData("a/b", false)]
    public void IsValidKey_Value_MatchesAllowedCharacters(string? key, bool expected) =>
        IdempotencyFilter.IsValidKey(key).ShouldBe(expected);

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, true)]
    [InlineData(400, true)]
    [InlineData(404, true)]
    [InlineData(422, true)]
    [InlineData(409, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(302, false)]
    public void ShouldStore_StatusCode_MatchesPolicy(int status, bool expected) =>
        IdempotencyFilter.ShouldStore(status).ShouldBe(expected);

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("code").GetString();
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string? key,
        string body = """{"name":"a"}""",
        string sub = "user-1",
        string path = Route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Add("X-Test-Sub", sub);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(IdempotencyFilter.HeaderName, key);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private Task<WebApplication> StartAsync(
        string failMode = "Closed",
        Dictionary<string, string?>? extraConfig = null,
        Action<WebApplication>? map = null) =>
        WebTestHost.StartAsync(
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Idempotency:FailMode"] = failMode,
                });
                if (extraConfig is not null)
                {
                    builder.Configuration.AddInMemoryCollection(extraConfig);
                }

                builder.Services.AddSingleton<ICache>(_cache);
                builder.Services.AddIdempotency(builder.Configuration);
            },
            app => (map ?? MapDefault)(app));

    private void MapDefault(WebApplication app) =>
        app.MapPost(Route, async (ThingInput input, HttpContext http) =>
        {
            var n = Interlocked.Increment(ref _executions);
            if (_entered is not null && _release is not null)
            {
                _entered.TrySetResult();
                await _release.Task;
            }

            return _respond(n, http);
        }).WithIdempotency();

    internal sealed record ThingInput(string Name);
}
