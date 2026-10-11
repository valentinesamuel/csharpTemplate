using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Acme.Service.IntegrationTests.Fixtures;

/// <summary>Captures rendered log messages from every logger it creates (used to observe a retry in progress).</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => [.. _messages];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(_messages);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            messages.Enqueue(formatter(state, exception));
        }
    }
}
