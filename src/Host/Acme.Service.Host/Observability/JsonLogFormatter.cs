using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Acme.Service.BuildingBlocks.Logging;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;

namespace Acme.Service.Host.Observability;

/// <summary>
/// Writes every <see cref="LogEvent"/> as exactly one single-line JSON object with a fixed set of 14 top-level keys
/// (<c>timestamp, level, message, requestId, correlationId, traceId, spanId, service, environment, logger, eventId,
/// meta, requestMeta, exception</c>), followed by one <c>\n</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>message</c> is rendered with the invariant culture and string values written literally (no quotes).
/// <c>meta</c> holds call-site properties only (names in <see cref="LogPropertyNames"/> are excluded);
/// <c>requestMeta</c> holds the dotted <c>Http.*</c> context.
/// </para>
/// <para>
/// The formatter never throws and never writes a partial line: each event is formatted into a private buffer, and
/// on failure a schema-conformant fallback line (then a constant line) is written and the failure reported through
/// <see cref="SelfLog"/>. It holds no mutable state, so one instance can be shared by concurrent sinks.
/// </para>
/// </remarks>
internal sealed class JsonLogFormatter(string? service, string? environment) : ITextFormatter
{
    private const int MaxDepth = 10;
    private const string DepthExceeded = "...";
    private const string LineTerminator = "\n";
    private const string FallbackMessage = "Log event could not be formatted";

    // Last resort when even the fallback line cannot be produced. All 14 keys, valid JSON, no dynamic content.
    private const string ConstantLine =
        "{\"timestamp\":null,\"level\":\"Error\",\"message\":\"Log event could not be formatted\",\"requestId\":null," +
        "\"correlationId\":null,\"traceId\":null,\"spanId\":null,\"service\":null,\"environment\":null,\"logger\":null," +
        "\"eventId\":null,\"meta\":null,\"requestMeta\":null,\"exception\":null}";

    // Relaxed: keep non-ASCII and HTML-sensitive characters readable. Control characters and the line/paragraph
    // separators (U+0085, U+2028, U+2029) are still escaped, so a line never breaks inside the JSON.
    private static readonly JsonWriterOptions _writerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly string? _service = string.IsNullOrWhiteSpace(service) ? null : service;
    private readonly string? _environment = string.IsNullOrWhiteSpace(environment) ? null : environment;

    public void Format(LogEvent logEvent, TextWriter output)
    {
        if (output is null)
        {
            return;
        }

        var line = BuildLine(logEvent);

        try
        {
            output.Write(line);
        }
#pragma warning disable CA1031 // A logging formatter must never throw; the failure is reported through SelfLog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SelfLog.WriteLine("JsonLogFormatter could not write a log line: {0}", ex);
        }
    }

#pragma warning disable CA1031 // A logging formatter must never throw; failures are reported through SelfLog.
    private string BuildLine(LogEvent? logEvent)
    {
        if (logEvent is null)
        {
            SelfLog.WriteLine("JsonLogFormatter received a null log event");
            return ConstantLine + LineTerminator;
        }

        try
        {
            return Render(writer => WriteEvent(writer, logEvent));
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("JsonLogFormatter could not format a log event, writing the fallback line: {0}", ex);
            try
            {
                return Render(writer => WriteFallback(writer, logEvent, ex));
            }
            catch (Exception fallbackEx)
            {
                SelfLog.WriteLine("JsonLogFormatter could not write the fallback line: {0}", fallbackEx);
                return ConstantLine + LineTerminator;
            }
        }
    }
#pragma warning restore CA1031

    private static string Render(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            write(writer);
            writer.Flush();
        }

        // Only a complete JSON object becomes a line; nothing reaches the output before this point.
        return string.Concat(Encoding.UTF8.GetString(buffer.WrittenSpan), LineTerminator);
    }

    private void WriteEvent(Utf8JsonWriter writer, LogEvent logEvent)
    {
        var properties = logEvent.Properties;
        var isSummary = properties.ContainsKey(LogPropertyNames.HttpStatusCode);
        var hasHttpContext = isSummary || properties.ContainsKey(LogPropertyNames.HttpMethod);

        writer.WriteStartObject();
        writer.WriteString("timestamp", FormatTimestamp(logEvent.Timestamp));
        writer.WriteString("level", LevelName(logEvent.Level));
        writer.WriteString("message", Sanitize(RenderMessage(logEvent)));
        WriteNullableString(writer, "requestId", ScalarText(properties, LogPropertyNames.RequestId));
        WriteNullableString(writer, "correlationId", ScalarText(properties, LogPropertyNames.CorrelationId));
        WriteNullableString(writer, "traceId", logEvent.TraceId?.ToHexString());
        WriteNullableString(writer, "spanId", logEvent.SpanId?.ToHexString());
        WriteNullableString(writer, "service", _service);
        WriteNullableString(writer, "environment", _environment);
        WriteNullableString(writer, "logger", ScalarText(properties, LogPropertyNames.SourceContext));
        WriteEventId(writer, properties);
        WriteMeta(writer, properties, isSummary, hasHttpContext);
        WriteRequestMeta(writer, logEvent, isSummary, hasHttpContext);
        WriteNullableString(writer, "exception", logEvent.Exception?.ToString());
        writer.WriteEndObject();
    }

    private void WriteFallback(Utf8JsonWriter writer, LogEvent logEvent, Exception error)
    {
        writer.WriteStartObject();
        writer.WriteString("timestamp", FormatTimestamp(logEvent.Timestamp));
        writer.WriteString("level", LevelName(logEvent.Level));
        writer.WriteString("message", FallbackMessage);
        writer.WriteNull("requestId");
        writer.WriteNull("correlationId");
        writer.WriteNull("traceId");
        writer.WriteNull("spanId");
        WriteNullableString(writer, "service", _service);
        WriteNullableString(writer, "environment", _environment);
        writer.WriteNull("logger");
        writer.WriteNull("eventId");
        writer.WritePropertyName("meta");
        writer.WriteStartObject();
        writer.WriteString("formatterError", Sanitize(error.GetType().Name));
        writer.WriteString("messageTemplate", Sanitize(logEvent.MessageTemplate?.Text ?? string.Empty));
        writer.WriteEndObject();
        writer.WriteNull("requestMeta");
        writer.WriteNull("exception");
        writer.WriteEndObject();
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string LevelName(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "Verbose",
        LogEventLevel.Debug => "Debug",
        LogEventLevel.Information => "Information",
        LogEventLevel.Warning => "Warning",
        LogEventLevel.Error => "Error",
        LogEventLevel.Fatal => "Fatal",
        _ => ((int)level).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Renders the template like Serilog's <c>{Message:l}</c>: string values without quotes, everything else in
    /// Serilog's text form with the invariant culture; a missing property keeps its raw token text.
    /// </summary>
    private static string RenderMessage(LogEvent logEvent)
    {
        using var message = new StringWriter(CultureInfo.InvariantCulture);
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            switch (token)
            {
                case TextToken text:
                    message.Write(text.Text);
                    break;

                case PropertyToken property
                    when property.Format is null
                    && logEvent.Properties.TryGetValue(property.PropertyName, out var value)
                    && value is ScalarValue { Value: string literal }:
                    WriteAligned(message, literal, property.Alignment);
                    break;

                default:
                    token.Render(logEvent.Properties, message, CultureInfo.InvariantCulture);
                    break;
            }
        }

        return message.ToString();
    }

    private static void WriteAligned(TextWriter output, string value, Alignment? alignment)
    {
        var padding = alignment is { } a ? a.Width - value.Length : 0;
        if (padding > 0 && alignment!.Value.Direction == AlignmentDirection.Right)
        {
            output.Write(new string(' ', padding));
        }

        output.Write(value);

        if (padding > 0 && alignment!.Value.Direction == AlignmentDirection.Left)
        {
            output.Write(new string(' ', padding));
        }
    }

    private static string? ScalarText(IReadOnlyDictionary<string, LogEventPropertyValue> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            return null;
        }

        return value switch
        {
            ScalarValue { Value: null } => null,
            ScalarValue { Value: string text } => text,
            ScalarValue scalar => Convert.ToString(scalar.Value, CultureInfo.InvariantCulture),
            _ => value.ToString(null, CultureInfo.InvariantCulture),
        };
    }

    private static void WriteEventId(Utf8JsonWriter writer, IReadOnlyDictionary<string, LogEventPropertyValue> properties)
    {
        long? id = null;
        if (properties.TryGetValue(LogPropertyNames.EventId, out var value))
        {
            id = value switch
            {
                StructureValue structure => structure.Properties
                    .Where(p => p.Name == "Id")
                    .Select(p => p.Value is ScalarValue scalar ? AsInt64(scalar.Value) : null)
                    .FirstOrDefault(),
                ScalarValue scalar => AsInt64(scalar.Value),
                _ => null,
            };
        }

        if (id is { } eventId)
        {
            writer.WriteNumber("eventId", eventId);
        }
        else
        {
            writer.WriteNull("eventId");
        }
    }

    private static long? AsInt64(object? value) => value switch
    {
        int v => v,
        long v => v,
        short v => v,
        byte v => v,
        sbyte v => v,
        ushort v => v,
        uint v => v,
        ulong v when v <= long.MaxValue => (long)v,
        _ => null,
    };

    private static void WriteMeta(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, LogEventPropertyValue> properties,
        bool isSummary,
        bool hasHttpContext)
    {
        var callSite = properties.Where(p => IsCallSiteProperty(p.Key, isSummary)).ToList();
        if (callSite.Count == 0 && !hasHttpContext)
        {
            writer.WriteNull("meta");
            return;
        }

        writer.WritePropertyName("meta");
        writer.WriteStartObject();
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in callSite)
        {
            if (TryClaimKey(written, name, camelCase: true) is { } key)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, value, depth: 1);
            }
        }

        writer.WriteEndObject();
    }

    private static bool IsCallSiteProperty(string name, bool isSummary) =>
        !LogPropertyNames.AlwaysReserved.Contains(name)
        && !(isSummary && LogPropertyNames.SummaryOnlyReserved.Contains(name))
        && !IsPositional(name);

    private static bool IsPositional(string name) => name.Length > 0 && name.All(char.IsAsciiDigit);

    /// <summary>
    /// Returns the key to write: the camelCased name, else the original name if the camelCased one is taken, else
    /// <see langword="null"/> (skip) so the object never has duplicate keys.
    /// </summary>
    private static string? TryClaimKey(HashSet<string> written, string name, bool camelCase)
    {
        if (camelCase)
        {
            var converted = Sanitize(JsonNamingPolicy.CamelCase.ConvertName(name));
            if (written.Add(converted))
            {
                return converted;
            }
        }

        var original = Sanitize(name);
        return written.Add(original) ? original : null;
    }

    private static void WriteValue(Utf8JsonWriter writer, LogEventPropertyValue value, int depth)
    {
        if (depth > MaxDepth)
        {
            writer.WriteStringValue(DepthExceeded);
            return;
        }

        switch (value)
        {
            case ScalarValue scalar:
                WriteScalar(writer, scalar.Value);
                break;

            case SequenceValue sequence:
                writer.WriteStartArray();
                foreach (var element in sequence.Elements)
                {
                    WriteValue(writer, element, depth + 1);
                }

                writer.WriteEndArray();
                break;

            case StructureValue structure:
                writer.WriteStartObject();
                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in structure.Properties)
                {
                    if (TryClaimKey(members, member.Name, camelCase: true) is { } key)
                    {
                        writer.WritePropertyName(key);
                        WriteValue(writer, member.Value, depth + 1);
                    }
                }

                writer.WriteEndObject();
                break;

            case DictionaryValue dictionary:
                writer.WriteStartObject();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (entryKey, entryValue) in dictionary.Elements)
                {
                    if (TryClaimKey(keys, ScalarString(entryKey.Value) ?? "null", camelCase: false) is { } key)
                    {
                        writer.WritePropertyName(key);
                        WriteValue(writer, entryValue, depth + 1);
                    }
                }

                writer.WriteEndObject();
                break;

            default:
                writer.WriteStringValue(Sanitize(value.ToString(null, CultureInfo.InvariantCulture)));
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool v:
                writer.WriteBooleanValue(v);
                break;
            case int v:
                writer.WriteNumberValue(v);
                break;
            case long v:
                writer.WriteNumberValue(v);
                break;
            case short v:
                writer.WriteNumberValue(v);
                break;
            case byte v:
                writer.WriteNumberValue(v);
                break;
            case sbyte v:
                writer.WriteNumberValue(v);
                break;
            case ushort v:
                writer.WriteNumberValue(v);
                break;
            case uint v:
                writer.WriteNumberValue(v);
                break;
            case ulong v:
                writer.WriteNumberValue(v);
                break;
            case decimal v:
                writer.WriteNumberValue(v);
                break;
            case double v when double.IsFinite(v):
                writer.WriteNumberValue(v);
                break;
            case float v when float.IsFinite(v):
                writer.WriteNumberValue(v);
                break;
            default:
                writer.WriteStringValue(Sanitize(ScalarString(value) ?? string.Empty));
                break;
        }
    }

    /// <summary>The invariant string form of a scalar (S3 value mapping for every non-JSON-native type).</summary>
    private static string? ScalarString(object? value) => value switch
    {
        null => null,
        string v => v,
        char v => v.ToString(),
        double v when double.IsNaN(v) => "NaN",
        double v when double.IsPositiveInfinity(v) => "Infinity",
        double v when double.IsNegativeInfinity(v) => "-Infinity",
        float v when float.IsNaN(v) => "NaN",
        float v when float.IsPositiveInfinity(v) => "Infinity",
        float v when float.IsNegativeInfinity(v) => "-Infinity",
        DateTime v => v.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset v => v.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan v => v.ToString("c", CultureInfo.InvariantCulture),
        Guid v => v.ToString("D"),
        Enum v => v.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static void WriteRequestMeta(Utf8JsonWriter writer, LogEvent logEvent, bool isSummary, bool hasHttpContext)
    {
        if (!hasHttpContext)
        {
            writer.WriteNull("requestMeta");
            return;
        }

        var properties = logEvent.Properties;
        writer.WritePropertyName("requestMeta");
        writer.WriteStartObject();
        WriteNullableString(writer, "method", ScalarText(properties, LogPropertyNames.HttpMethod));
        WriteNullableString(writer, "path", ScalarText(properties, LogPropertyNames.HttpPath));

        var statusCode = properties.TryGetValue(LogPropertyNames.HttpStatusCode, out var status) && status is ScalarValue s
            ? AsInt64(s.Value)
            : null;
        if (statusCode is { } code)
        {
            writer.WriteNumber("statusCode", code);
        }
        else
        {
            writer.WriteNull("statusCode");
        }

        writer.WritePropertyName("durationInMs");
        if (properties.TryGetValue(LogPropertyNames.HttpDurationMs, out var duration)
            && duration is ScalarValue { Value: double or float or decimal or int or long or uint or ulong or short or ushort or byte or sbyte } d)
        {
            WriteScalar(writer, d.Value);
        }
        else
        {
            writer.WriteNullValue();
        }

        // isSuccess belongs to the request-summary event only; an exception always means failure.
        var hasIsSuccess = properties.TryGetValue(LogPropertyNames.HttpIsSuccess, out var isSuccess);
        if ((isSummary || hasIsSuccess) && logEvent.Exception is not null)
        {
            writer.WriteBoolean("isSuccess", false);
        }
        else if (hasIsSuccess && isSuccess is ScalarValue { Value: bool success })
        {
            writer.WriteBoolean("isSuccess", success);
        }
        else
        {
            writer.WriteNull("isSuccess");
        }

        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, Sanitize(value));
        }
    }

    /// <summary>Replaces lone UTF-16 surrogates with U+FFFD; <see cref="Utf8JsonWriter"/> rejects them.</summary>
    private static string Sanitize(string value)
    {
        var index = FirstLoneSurrogate(value, 0);
        if (index < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value);
        while (index >= 0)
        {
            builder[index] = '\uFFFD';
            index = FirstLoneSurrogate(value, index + 1);
        }

        return builder.ToString();
    }

    private static int FirstLoneSurrogate(string value, int start)
    {
        for (var i = start; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                    continue;
                }

                return i;
            }

            if (char.IsLowSurrogate(c))
            {
                return i;
            }
        }

        return -1;
    }
}
