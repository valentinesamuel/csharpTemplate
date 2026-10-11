using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Logging;
using Serilog.Context;

namespace Acme.Service.Host.Commands;

/// <summary>
/// Log context of one command-verb invocation (S6, LOG-09): every event logged while the returned scope is open, through
/// the bootstrap logger or the <see cref="CommandHost"/> logger (both use <c>Enrich.FromLogContext</c>), carries the
/// same generated correlation id. Used only around verb dispatch, never around the web host.
/// </summary>
internal static class CliLogContext
{
    /// <summary>Pushes a new correlation id (32 lowercase hex) onto the Serilog log context.</summary>
    /// <returns>A scope that removes the property when disposed.</returns>
    public static IDisposable BeginInvocation() =>
        LogContext.PushProperty(LogPropertyNames.CorrelationId, CorrelationIdGenerator.Generate());
}
