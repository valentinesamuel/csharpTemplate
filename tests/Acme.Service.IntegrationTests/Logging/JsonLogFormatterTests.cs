using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acme.Service.Host.Observability;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;

namespace Acme.Service.IntegrationTests.Logging;

/// <summary>
/// Pure unit tests of the JSON line contract (plan Phase 2, criteria 1 to 17). No host, no Docker.
/// The class runs in a non-parallel collection because one test enables the process-global <see cref="SelfLog"/>.
/// </summary>
[Collection(JsonLogFormatterGroup.Name)]
public sealed partial class JsonLogFormatterTests
{
    private static readonly string[] _keys =
    [
        "timestamp", "level", "message", "requestId", "correlationId", "traceId", "spanId",
        "service", "environment", "logger", "eventId", "meta", "requestMeta", "exception",
    ];

    private static readonly MessageTemplateParser _parser = new();

    private static readonly DateTimeOffset _timestamp = new(2026, 10, 11, 12, 34, 56, 789, TimeSpan.FromHours(2));

    [Fact]
    public void Format_MinimalEvent_WritesAllKeysInOrderWithNullsAndUtcTimestamp()
    {
        var output = Format(Event("Started"));

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.EnumerateObject().Select(p => p.Name).ShouldBe(_keys);
        foreach (var key in new[] { "requestId", "correlationId", "traceId", "spanId", "logger", "eventId", "meta", "requestMeta", "exception" })
        {
            root.GetProperty(key).ValueKind.ShouldBe(JsonValueKind.Null, key);
        }

        var timestamp = root.GetProperty("timestamp").GetString();
        timestamp.ShouldNotBeNull();
        TimestampPattern().IsMatch(timestamp).ShouldBeTrue(timestamp);
        timestamp.ShouldBe("2026-10-11T10:34:56.789Z");
        root.GetProperty("level").GetString().ShouldBe("Information");
        root.GetProperty("message").GetString().ShouldBe("Started");
        root.GetProperty("service").GetString().ShouldBe("acme-service");
        root.GetProperty("environment").GetString().ShouldBe("Testing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("de-DE")]
    public void Format_StringAndDecimalHoles_RendersLiteralMessageAndTypedMeta(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var root = FormatRoot(Event(
                "Order {OrderId} placed by {Email} for {TotalAmount}",
                Prop("OrderId", "o-42"),
                Prop("Email", "a@b.com"),
                Prop("TotalAmount", 19.99m)));

            root.GetProperty("message").GetString().ShouldBe("Order o-42 placed by a@b.com for 19.99");
            var meta = root.GetProperty("meta");
            meta.EnumerateObject().Select(p => p.Name).ShouldBe(["orderId", "email", "totalAmount"]);
            meta.GetProperty("orderId").GetString().ShouldBe("o-42");
            meta.GetProperty("email").GetString().ShouldBe("a@b.com");
            meta.GetProperty("totalAmount").ValueKind.ShouldBe(JsonValueKind.Number);
            meta.GetProperty("totalAmount").GetDecimal().ShouldBe(19.99m);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Format_ReservedProperties_AreExcludedFromMetaAndMappedToTopLevel()
    {
        var root = FormatRoot(Event(
            "Placed {OrderId}",
            Prop("CorrelationId", "corr-1"),
            Prop("RequestId", "req-1"),
            Prop("SourceContext", "Acme.Service.Orders"),
            new LogEventProperty("EventId", new StructureValue([Prop("Id", 1000), Prop("Name", "X")])),
            Prop("RequestPath", "/orders"),
            Prop("ConnectionId", "conn-1"),
            Prop("Scope", "scope"),
            Prop("ParentId", "parent-1"),
            Prop("{OriginalFormat}", "Placed {OrderId}"),
            Prop("OrderId", "o-1")));

        MetaKeys(root).ShouldBe(["orderId"]);
        root.GetProperty("meta").GetProperty("orderId").GetString().ShouldBe("o-1");
        root.GetProperty("correlationId").GetString().ShouldBe("corr-1");
        root.GetProperty("requestId").GetString().ShouldBe("req-1");
        root.GetProperty("logger").GetString().ShouldBe("Acme.Service.Orders");
        root.GetProperty("eventId").GetInt32().ShouldBe(1000);
    }

    [Fact]
    public void Format_SummaryOnlyNamesWithoutHttpStatusCode_AreCallSiteMeta()
    {
        var root = FormatRoot(Event(
            "Call {Method} {Path}",
            Prop("Method", "POST"),
            Prop("Path", "/login"),
            Prop("StatusCode", 429),
            Prop("Elapsed", 1.5),
            Prop("RequestMethod", "POST")));

        MetaKeys(root).ShouldBe(["method", "path", "statusCode", "elapsed", "requestMethod"]);
        root.GetProperty("requestMeta").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Format_SummaryEvent_WritesRequestMetaAndEmptyMeta()
    {
        var root = FormatRoot(SummaryEvent(exception: null));

        root.GetProperty("message").GetString().ShouldBe("HTTP GET /orders responded 201 in 12.3000 ms");
        root.GetProperty("meta").EnumerateObject().ShouldBeEmpty();
        root.GetProperty("meta").ValueKind.ShouldBe(JsonValueKind.Object);
        root.GetProperty("requestMeta").GetRawText()
            .ShouldBe("""{"method":"GET","path":"/orders","statusCode":201,"durationInMs":12.3,"isSuccess":true}""");
    }

    [Fact]
    public void Format_SummaryEventWithException_ForcesIsSuccessFalseAndWritesException()
    {
        var root = FormatRoot(SummaryEvent(Thrown()));

        root.GetProperty("requestMeta").GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
        var exception = root.GetProperty("exception").GetString();
        exception.ShouldNotBeNull();
        exception.ShouldContain(nameof(InvalidOperationException));
        exception.ShouldContain(nameof(Thrown));
    }

    [Fact]
    public void Format_NonSummaryHttpLine_WritesFiveRequestMetaKeysWithNullsAndEmptyMeta()
    {
        var root = FormatRoot(Event(
            "Handled",
            Prop("Http.Method", "GET"),
            Prop("Http.Path", "/orders")));

        var requestMeta = root.GetProperty("requestMeta");
        requestMeta.EnumerateObject().Select(p => p.Name).ShouldBe(["method", "path", "statusCode", "durationInMs", "isSuccess"]);
        requestMeta.GetProperty("method").GetString().ShouldBe("GET");
        requestMeta.GetProperty("path").GetString().ShouldBe("/orders");
        requestMeta.GetProperty("statusCode").ValueKind.ShouldBe(JsonValueKind.Null);
        requestMeta.GetProperty("durationInMs").ValueKind.ShouldBe(JsonValueKind.Null);
        requestMeta.GetProperty("isSuccess").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("meta").ValueKind.ShouldBe(JsonValueKind.Object);
        root.GetProperty("meta").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public void Format_NonHttpLineWithoutCallSiteProperties_WritesNullMetaAndRequestMeta()
    {
        var root = FormatRoot(Event("Job ran", Prop("CorrelationId", "corr-1")));

        root.GetProperty("meta").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("requestMeta").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Format_SpecialCharacters_AreEscapedOnOneLineAndRoundTrip()
    {
        const string special = "q\"b\\c\r\nd\te\u0000f\u2028g\u2029h\u0085i";
        var output = Format(Event("T " + special + " {Value}", Prop("Value", special)));

        output.ShouldEndWith("\n");
        var body = output[..^1];
        body.IndexOfAny(['\r', '\n', '\u0085', '\u2028', '\u2029']).ShouldBe(-1);
        output.Count(c => c == '\n').ShouldBe(1);

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("message").GetString().ShouldBe("T " + special + " " + special);
        json.RootElement.GetProperty("meta").GetProperty("value").GetString().ShouldBe(special);
    }

    [Fact]
    public void Format_LoneSurrogate_IsReplacedWithReplacementCharacter()
    {
        var root = FormatRoot(Event("Bad \uD800 {Value}", Prop("Value", "x\uD800y")));

        root.GetProperty("message").GetString().ShouldBe("Bad \uFFFD x\uFFFDy");
        root.GetProperty("meta").GetProperty("value").GetString().ShouldBe("x\uFFFDy");
    }

    [Fact]
    public void Format_NonFiniteDoubles_AreWrittenAsStrings()
    {
        var root = FormatRoot(Event(
            "Values",
            Prop("NotANumber", double.NaN),
            Prop("Positive", double.PositiveInfinity),
            Prop("Negative", double.NegativeInfinity)));

        var meta = root.GetProperty("meta");
        meta.GetProperty("notANumber").GetString().ShouldBe("NaN");
        meta.GetProperty("positive").GetString().ShouldBe("Infinity");
        meta.GetProperty("negative").GetString().ShouldBe("-Infinity");
    }

    [Fact]
    public void Format_PositionalHole_IsNotInMetaButIsInMessage()
    {
        var root = FormatRoot(Event("Value {0}", Prop("0", "forty-two")));

        root.GetProperty("message").GetString().ShouldBe("Value forty-two");
        root.GetProperty("meta").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Format_CamelCaseCollision_WritesOriginalNameAndNoDuplicateKeys()
    {
        // The collision rule is first-written-wins: the second property falls back to its original name.
        var root = FormatRoot(Event("Collide", Prop("orderId", "lower"), Prop("OrderId", "upper")));

        MetaKeys(root).ShouldBe(["orderId", "OrderId"]);
        root.GetProperty("meta").GetProperty("orderId").GetString().ShouldBe("lower");
        root.GetProperty("meta").GetProperty("OrderId").GetString().ShouldBe("upper");
    }

    [Fact]
    public void Format_DestructuredObject_WritesCamelCasedNestedObjectAndDepthGuard()
    {
        var logEvent = Capture(logger => logger.Information(
            "Placed {@Order}",
            new OrderSample("o-1", new CustomerSample("Ada"), ["a", "b"])));

        var order = FormatRoot(logEvent).GetProperty("meta").GetProperty("order");
        order.EnumerateObject().Select(p => p.Name).ShouldBe(["orderId", "customer", "lines"]);
        order.GetProperty("customer").GetProperty("name").GetString().ShouldBe("Ada");
        order.GetProperty("lines").EnumerateArray().Select(e => e.GetString()).ShouldBe(["a", "b"]);
        order.GetRawText().ShouldNotContain("$type");

        LogEventPropertyValue nested = new ScalarValue("leaf");
        for (var i = 0; i < 15; i++)
        {
            nested = new StructureValue([new LogEventProperty("Child", nested)]);
        }

        var deep = FormatRoot(Event("Deep", new LogEventProperty("Deep", nested))).GetProperty("meta").GetProperty("deep");
        var levels = 1;
        while (deep.ValueKind == JsonValueKind.Object)
        {
            deep = deep.GetProperty("child");
            levels++;
        }

        deep.GetString().ShouldBe("...");
        levels.ShouldBe(11);
    }

    [Fact]
    public void Format_NonPrimitiveScalars_AreInvariantStrings()
    {
        object[] values =
        [
            new PathString("/orders"),
            new Uri("https://example.test/a"),
            new Version(1, 2, 3),
            IPAddress.Parse("10.0.0.1"),
            new CustomText(),
        ];
        string[] expected = ["/orders", "https://example.test/a", "1.2.3", "10.0.0.1", CustomText.Text];

        // Hand-built scalars.
        AssertScalars(Event(
            "Scalars",
            values.Select((v, i) => new LogEventProperty("V" + i.ToString(CultureInfo.InvariantCulture), new ScalarValue(v))).ToArray()));

        // Captured by a real logger under a comma-decimal culture.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            AssertScalars(Capture(logger => logger.Information("Scalars {V0} {V1} {V2} {V3} {V4}", values)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        void AssertScalars(LogEvent logEvent)
        {
            var root = FormatRoot(logEvent);
            root.GetProperty("message").GetString().ShouldNotBe("Log event could not be formatted");
            var meta = root.GetProperty("meta");
            for (var i = 0; i < expected.Length; i++)
            {
                var value = meta.GetProperty("v" + i.ToString(CultureInfo.InvariantCulture));
                value.ValueKind.ShouldBe(JsonValueKind.String);
                value.GetString().ShouldBe(expected[i]);
            }
        }
    }

    [Fact]
    public void Format_ValueToStringThrows_WritesFallbackLineAndReportsToSelfLog()
    {
        using var selfLog = new StringWriter(CultureInfo.InvariantCulture);
        SelfLog.Enable(TextWriter.Synchronized(selfLog));
        string output;
        try
        {
            output = Format(Event("Bad {Value}", new LogEventProperty("Value", new ScalarValue(new ThrowingToString()))));
        }
        finally
        {
            SelfLog.Disable();
        }

        output.Count(c => c == '\n').ShouldBe(1);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.EnumerateObject().Select(p => p.Name).ShouldBe(_keys);
        root.GetProperty("message").GetString().ShouldBe("Log event could not be formatted");
        root.GetProperty("meta").GetProperty("formatterError").GetString().ShouldBe(nameof(NotSupportedException));
        root.GetProperty("meta").GetProperty("messageTemplate").GetString().ShouldBe("Bad {Value}");
        root.GetProperty("service").GetString().ShouldBe("acme-service");
        selfLog.ToString().ShouldContain(nameof(NotSupportedException));
    }

    [Fact]
    public async Task Format_ConcurrentThreads_EveryLineParses()
    {
        var formatter = new JsonLogFormatter("acme-service", "Testing");
        var start = new Barrier(8);

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Factory.StartNew(
            () =>
            {
                using var output = new StringWriter(CultureInfo.InvariantCulture);
                start.SignalAndWait(TestContext.Current.CancellationToken);
                for (var i = 0; i < 1000; i++)
                {
                    formatter.Format(
                        Event(
                            "Item {Worker} {Index} {Payload}",
                            Prop("Worker", worker),
                            Prop("Index", i),
                            Prop("Payload", "line\n" + i.ToString(CultureInfo.InvariantCulture)),
                            Prop("Http.Method", "GET")),
                        output);
                }

                return output.ToString();
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();

        var results = await Task.WhenAll(workers);
        start.Dispose();

        foreach (var (text, worker) in results.Select((t, w) => (t, w)))
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBe(1000);
            for (var i = 0; i < lines.Length; i++)
            {
                using var json = JsonDocument.Parse(lines[i]);
                json.RootElement.GetProperty("meta").GetProperty("worker").GetInt32().ShouldBe(worker);
                json.RootElement.GetProperty("meta").GetProperty("index").GetInt32().ShouldBe(i);
            }
        }
    }

    private static string Format(LogEvent logEvent)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        new JsonLogFormatter("acme-service", "Testing").Format(logEvent, output);
        return output.ToString();
    }

    private static JsonElement FormatRoot(LogEvent logEvent)
    {
        var output = Format(logEvent);
        output.ShouldEndWith("\n");
        using var json = JsonDocument.Parse(output);
        return json.RootElement.Clone();
    }

    private static string[] MetaKeys(JsonElement root) =>
        root.GetProperty("meta").EnumerateObject().Select(p => p.Name).ToArray();

    private static LogEvent Event(string template, params LogEventProperty[] properties) =>
        new(_timestamp, LogEventLevel.Information, null, _parser.Parse(template), properties);

    private static LogEventProperty Prop(string name, object? value) => new(name, new ScalarValue(value));

    private static LogEvent SummaryEvent(Exception? exception) =>
        new(
            _timestamp,
            exception is null ? LogEventLevel.Information : LogEventLevel.Error,
            exception,
            _parser.Parse("HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms"),
            [
                Prop("RequestMethod", "GET"),
                Prop("RequestPath", "/orders"),
                Prop("StatusCode", 201),
                Prop("Elapsed", 12.3),
                Prop("Http.Method", "GET"),
                Prop("Http.Path", "/orders"),
                Prop("Http.StatusCode", 201),
                Prop("Http.DurationMs", 12.3),
                Prop("Http.IsSuccess", true),
            ]);

    private static LogEvent Capture(Action<Serilog.ILogger> log)
    {
        var sink = new CapturingSink();
        using (var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger())
        {
            log(logger);
        }

        return sink.Events.ShouldHaveSingleItem();
    }

    private static InvalidOperationException Thrown()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")]
    private static partial Regex TimestampPattern();

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed record CustomerSample(string Name);

    private sealed record OrderSample(string OrderId, CustomerSample Customer, IReadOnlyList<string> Lines);

    private sealed class CustomText
    {
        public const string Text = "custom-text";

        public override string ToString() => Text;
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new NotSupportedException("ToString failed");
    }
}

/// <summary>Runs <see cref="JsonLogFormatterTests"/> alone: one test enables the process-global SelfLog.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JsonLogFormatterGroup
{
    public const string Name = "json-log-formatter";
}
