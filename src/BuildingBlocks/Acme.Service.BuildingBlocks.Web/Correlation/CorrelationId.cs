using System.Diagnostics;

namespace Acme.Service.BuildingBlocks.Web.Correlation;

/// <summary>Correlation id rules (D-25): header name, validation and generation.</summary>
public static class CorrelationId
{
    /// <summary>The HTTP header carrying the correlation id, inbound and outbound.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>The log scope / ProblemDetails property name.</summary>
    public const string PropertyName = "CorrelationId";

    /// <summary>Maximum accepted length of an inbound correlation id.</summary>
    public const int MaxLength = 64;

    /// <summary>True when <paramref name="value"/> is 1 to 64 characters of <c>[A-Za-z0-9-_]</c>.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Generates a correlation id from the current trace id, or a new random trace id when no activity is in scope.</summary>
    public static string Generate() =>
        (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToHexString();
}
