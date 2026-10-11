using System.Diagnostics;
using Acme.Service.BuildingBlocks.Results;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.UseCases.Behaviors;

/// <summary>
/// Outermost behaviour: logs the use-case name, outcome and duration. Never logs the input, which may
/// contain secrets (INV-28). Exceptions propagate untouched and are logged by the caller's global handler.
/// </summary>
/// <typeparam name="TIn">Input type.</typeparam>
/// <typeparam name="TOut">Success value type.</typeparam>
public sealed class LoggingBehavior<TIn, TOut>(ILogger<LoggingBehavior<TIn, TOut>> logger) : IUseCaseBehavior<TIn, TOut>
    where TIn : notnull
{
    /// <inheritdoc />
    public async Task<Result<TOut>> HandleAsync(TIn input, UseCaseNext<TOut> continuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        var useCase = typeof(TIn).Name;
        var started = Stopwatch.GetTimestamp();
        UseCaseLog.Executing(logger, useCase);

        var result = await continuation(cancellationToken).ConfigureAwait(false);

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (result.IsSuccess)
        {
            UseCaseLog.Succeeded(logger, useCase, elapsedMs);
        }
        else
        {
            UseCaseLog.Failed(logger, useCase, result.Error.Code, result.Error.Type, elapsedMs);
        }

        return result;
    }
}

internal static partial class UseCaseLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Executing use case {UseCase}")]
    public static partial void Executing(ILogger logger, string useCase);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Use case {UseCase} succeeded in {ElapsedMs:0.0} ms")]
    public static partial void Succeeded(ILogger logger, string useCase, double elapsedMs);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Use case {UseCase} failed with {ErrorCode} ({ErrorType}) in {ElapsedMs:0.0} ms")]
    public static partial void Failed(ILogger logger, string useCase, string errorCode, ErrorType errorType, double elapsedMs);
}
