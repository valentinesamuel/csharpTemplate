using System.Diagnostics;

namespace Acme.Service.BuildingBlocks.Correlation;

/// <summary>Generates correlation ids for operations that start without one (D-25).</summary>
public static class CorrelationIdGenerator
{
    /// <summary>
    /// Returns the current trace id, or a new random trace id when no activity is in scope, as 32 lowercase hex characters.
    /// </summary>
    public static string Generate() =>
        (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToHexString();
}
