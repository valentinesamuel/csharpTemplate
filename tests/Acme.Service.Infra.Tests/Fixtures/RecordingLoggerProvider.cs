using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Infra.Tests.Fixtures;

/// <summary>Captures rendered log entries from every logger it creates.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

    /// <summary>A typed logger writing to this provider.</summary>
    public ILogger<T> CreateLogger<T>() => new TypedLogger<T>(CreateLogger(typeof(T).FullName!));

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

/// <summary>One captured log entry.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);
