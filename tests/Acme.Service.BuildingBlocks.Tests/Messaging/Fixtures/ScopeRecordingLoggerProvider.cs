using System.Collections.Concurrent;
using Acme.Service.BuildingBlocks.Logging;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Tests.Messaging.Fixtures;

/// <summary>
/// Captures every log entry together with the key/value pairs of the scopes active when it was written (the logger
/// factory's external scopes), so tests can assert what a structured sink would see without a formatter.
/// </summary>
internal sealed class ScopeRecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<ScopedLogEntry> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<ScopedLogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new ScopedLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopes = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));

    public void Dispose()
    {
    }

    private void Record(string category, LogLevel level, string message, Exception? exception)
    {
        // Outer scopes first; an inner scope overrides a key it shares with an outer one.
        var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
        _scopes.ForEachScope(
            static (state, values) =>
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                    {
                        values[pair.Key] = pair.Value;
                    }
                }
            },
            scope);
        _entries.Enqueue(new ScopedLogEntry(category, level, message, exception, scope));
    }

    private sealed class ScopedLogger(ScopeRecordingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            provider.Record(category, logLevel, formatter(state, exception), exception);
        }
    }
}

/// <summary>One captured log entry with the merged key/value pairs of its active scopes.</summary>
internal sealed record ScopedLogEntry(
    string Category,
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Scope)
{
    /// <summary>The <c>CorrelationId</c> scope value as a string, or null when absent.</summary>
    public string? CorrelationId =>
        Scope.TryGetValue(LogPropertyNames.CorrelationId, out var value) ? value?.ToString() : null;
}
