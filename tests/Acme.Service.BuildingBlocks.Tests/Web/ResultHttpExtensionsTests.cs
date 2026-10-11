using System.Text.Json;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Web.Correlation;
using Acme.Service.BuildingBlocks.Web.Errors;
using Acme.Service.BuildingBlocks.Web.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Web;

public sealed class ResultHttpExtensionsTests
{
    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Unprocessable, 422)]
    [InlineData(ErrorType.TooManyRequests, 429)]
    [InlineData(ErrorType.Unavailable, 503)]
    public async Task ToHttpResult_Failure_MapsErrorTypeToStatusAndProblemJson(ErrorType type, int expectedStatus)
    {
        var result = Result.Failure(new Error("some_code", "Something failed.", type));

        var (http, json) = await ExecuteAsync(result.ToHttpResult());

        http.Response.StatusCode.ShouldBe(expectedStatus);
        http.Response.ContentType.ShouldBe("application/problem+json");
        json.GetProperty("status").GetInt32().ShouldBe(expectedStatus);
        json.GetProperty("code").GetString().ShouldBe("some_code");
        json.GetProperty("detail").GetString().ShouldBe("Something failed.");
    }

    [Fact]
    public async Task ToHttpResult_Failure_IncludesTraceIdAndCorrelationId()
    {
        var result = Result.Failure<int>(Error.NotFound("thing_not_found", "No such thing."));

        var (_, json) = await ExecuteAsync(result.ToHttpResult(), correlationId: "corr123");

        json.GetProperty("correlationId").GetString().ShouldBe("corr123");
        json.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ToHttpResult_ValidationFailure_WritesErrorsDictionaryFromDetails()
    {
        var details = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["name"] = ["Name is required."],
            ["total"] = ["Must be positive.", "Must be below 1000."],
        };
        var result = Result.Failure(Error.Validation("validation_failed", "Invalid input.", details));

        var (http, json) = await ExecuteAsync(result.ToHttpResult());

        http.Response.StatusCode.ShouldBe(400);
        json.GetProperty("code").GetString().ShouldBe("validation_failed");
        var errors = json.GetProperty("errors");
        errors.GetProperty("name")[0].GetString().ShouldBe("Name is required.");
        errors.GetProperty("total").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task ToHttpResult_Unavailable_SetsRetryAfter()
    {
        var result = Result.Failure(Error.Unavailable("db_unavailable", "Try again."));

        var (http, _) = await ExecuteAsync(result.ToHttpResult());

        http.Response.Headers.RetryAfter.ToString().ShouldBe(ResultHttpExtensions.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ToHttpResult_NonUnavailableFailure_HasNoRetryAfter()
    {
        var (http, _) = await ExecuteAsync(Result.Failure(Error.Conflict("c", "m")).ToHttpResult());

        http.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
    }

    [Fact]
    public async Task ToHttpResult_SuccessWithValue_Returns200WithBody()
    {
        var (http, json) = await ExecuteAsync(Result.Success(new Dto("x", 3)).ToHttpResult());

        http.Response.StatusCode.ShouldBe(200);
        json.GetProperty("data").GetProperty("name").GetString().ShouldBe("x");
        json.GetProperty("data").GetProperty("count").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task ToHttpResult_SuccessWithValue_WrapsDataInEnvelopeWithIds()
    {
        var (_, json) = await ExecuteAsync(Result.Success(new Dto("x", 3)).ToHttpResult(), correlationId: "corr123");

        json.GetProperty("isSuccess").GetBoolean().ShouldBeTrue();
        json.GetProperty("requestId").GetString().ShouldNotBeNull().ShouldMatch("^[A-Za-z0-9]+$");
        json.GetProperty("correlationId").GetString().ShouldBe("corr123");
        json.GetProperty("correlationId").GetString().ShouldNotBeNull().ShouldMatch("^[A-Za-z0-9]+$");
        json.GetProperty("data").GetProperty("name").GetString().ShouldBe("x");
    }

    [Fact]
    public async Task ToHttpResult_Failure_HasIsSuccessFalseAndRequestId()
    {
        var (_, json) = await ExecuteAsync(Result.Failure<int>(Error.NotFound("nf", "Missing.")).ToHttpResult());

        json.GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
        json.GetProperty("requestId").GetString().ShouldNotBeNull().ShouldMatch("^[A-Za-z0-9]+$");
    }

    [Fact]
    public async Task ToHttpResult_SuccessWithoutValue_Returns204()
    {
        var http = await ExecuteRawAsync(Result.Success().ToHttpResult());

        http.Response.StatusCode.ShouldBe(204);
        http.Response.Body.Length.ShouldBe(0);
    }

    [Fact]
    public async Task ToHttpResult_SuccessWithCallback_UsesCallbackResult()
    {
        var result = Result.Success(42).ToHttpResult(id => TypedResults.Created($"/things/{id}", id));

        var (http, json) = await ExecuteAsync(result);

        http.Response.StatusCode.ShouldBe(201);
        http.Response.Headers.Location.ToString().ShouldBe("/things/42");
        json.GetProperty("isSuccess").GetBoolean().ShouldBeTrue();
        json.GetProperty("data").GetInt32().ShouldBe(42);
    }

    private static async Task<(HttpContext Http, JsonElement Json)> ExecuteAsync(IResult result, string? correlationId = null)
    {
        var http = await ExecuteRawAsync(result, correlationId);
        http.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(http.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        return (http, document.RootElement.Clone());
    }

    private static async Task<HttpContext> ExecuteRawAsync(IResult result, string? correlationId = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddCorrelationId()
            .AddProblemDetailsSetup()
            .BuildServiceProvider();

        if (correlationId is not null)
        {
            services.GetRequiredService<ICorrelationIdAccessor>().CorrelationId = correlationId;
        }

        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        return http;
    }

    private sealed record Dto(string Name, int Count);
}
