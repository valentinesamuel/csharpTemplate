namespace Acme.Service.BuildingBlocks.Results;

/// <summary>
/// An expected, user-facing failure. <see cref="Code"/> is a stable machine-readable identifier
/// (snake_case); <see cref="Message"/> is safe to show to clients.
/// </summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Message">Client-safe description.</param>
/// <param name="Type">Error category.</param>
public sealed record Error(string Code, string Message, ErrorType Type)
{
    /// <summary>Per-field validation messages, keyed by field name. Only set for validation errors.</summary>
    public IReadOnlyDictionary<string, string[]>? Details { get; init; }

    /// <summary>Creates a <see cref="ErrorType.Validation"/> error.</summary>
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new(code, message, ErrorType.Validation) { Details = details };

    /// <summary>Creates an <see cref="ErrorType.Unauthorized"/> error.</summary>
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    /// <summary>Creates a <see cref="ErrorType.Forbidden"/> error.</summary>
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    /// <summary>Creates a <see cref="ErrorType.NotFound"/> error.</summary>
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    /// <summary>Creates a <see cref="ErrorType.Conflict"/> error.</summary>
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    /// <summary>Creates an <see cref="ErrorType.Unprocessable"/> error.</summary>
    public static Error Unprocessable(string code, string message) => new(code, message, ErrorType.Unprocessable);

    /// <summary>Creates a <see cref="ErrorType.TooManyRequests"/> error.</summary>
    public static Error TooManyRequests(string code, string message) => new(code, message, ErrorType.TooManyRequests);

    /// <summary>Creates an <see cref="ErrorType.Unavailable"/> error.</summary>
    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);
}
