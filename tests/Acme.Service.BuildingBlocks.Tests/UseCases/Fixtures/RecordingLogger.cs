using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Tests.UseCases.Fixtures;

/// <summary>Writes "log:&lt;EventName&gt;" entries into the shared <see cref="TraceLog"/>.</summary>
internal sealed class RecordingLogger<T>(TraceLog trace) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        trace.Add($"log:{eventId.Name}");
}
