using Microsoft.Extensions.Logging;
using HangfireLog = Hangfire.Logging.ILog;
using HangfireLogLevel = Hangfire.Logging.LogLevel;
using HangfireLogProviderBase = Hangfire.Logging.ILogProvider;

namespace Acme.Service.BuildingBlocks.Infra.Hangfire;

/// <summary>
/// Routes Hangfire's own logging (worker, scheduler and storage messages, including "schema missing" errors) to
/// <see cref="ILoggerFactory"/>. Hangfire's log provider is process-wide; unlike Hangfire's default ASP.NET Core
/// provider, this one degrades to a no-op instead of throwing once its container (and factory) has been disposed,
/// which matters when several hosts live in one process (tests).
/// </summary>
internal sealed partial class HangfireLogProvider(ILoggerFactory loggerFactory) : HangfireLogProviderBase
{
    /// <inheritdoc />
    public HangfireLog GetLogger(string name)
    {
        try
        {
            return new LoggerLog(loggerFactory.CreateLogger(name));
        }
        catch (ObjectDisposedException)
        {
            return NoOpLog.Instance;
        }
    }

    private static LogLevel ToLogLevel(HangfireLogLevel level) => level switch
    {
        HangfireLogLevel.Trace => LogLevel.Trace,
        HangfireLogLevel.Debug => LogLevel.Debug,
        HangfireLogLevel.Info => LogLevel.Information,
        HangfireLogLevel.Warn => LogLevel.Warning,
        HangfireLogLevel.Error => LogLevel.Error,
        HangfireLogLevel.Fatal => LogLevel.Critical,
        _ => LogLevel.None,
    };

    [LoggerMessage(Message = "{HangfireMessage}")]
    private static partial void LogMessage(ILogger logger, LogLevel level, Exception? exception, string hangfireMessage);

    private sealed class LoggerLog(ILogger logger) : HangfireLog
    {
        public bool Log(HangfireLogLevel logLevel, Func<string>? messageFunc, Exception? exception = null)
        {
            var level = ToLogLevel(logLevel);
            if (messageFunc is null)
            {
                return logger.IsEnabled(level);
            }

            if (!logger.IsEnabled(level))
            {
                return false;
            }

            try
            {
                var message = messageFunc();
                LogMessage(logger, level, exception, message);
            }
            catch (ObjectDisposedException)
            {
                // The owning container was disposed while Hangfire was still logging (tests); drop the message.
            }

            return true;
        }
    }

    private sealed class NoOpLog : HangfireLog
    {
        public static readonly NoOpLog Instance = new();

        public bool Log(HangfireLogLevel logLevel, Func<string>? messageFunc, Exception? exception = null) => false;
    }
}
