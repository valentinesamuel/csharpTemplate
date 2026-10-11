namespace Acme.Service.BuildingBlocks.Results;

/// <summary>Category of an expected failure. Mapped to an HTTP status by BuildingBlocks.Web.</summary>
public enum ErrorType
{
    /// <summary>The input is invalid (400).</summary>
    Validation,

    /// <summary>The caller is not authenticated (401).</summary>
    Unauthorized,

    /// <summary>The caller is authenticated but not allowed (403).</summary>
    Forbidden,

    /// <summary>The target resource does not exist (404).</summary>
    NotFound,

    /// <summary>The request conflicts with the current state (409).</summary>
    Conflict,

    /// <summary>The request is well-formed but violates a business rule (422).</summary>
    Unprocessable,

    /// <summary>The caller exceeded a rate limit (429).</summary>
    TooManyRequests,

    /// <summary>A dependency is temporarily unavailable; the caller may retry (503).</summary>
    Unavailable,
}
