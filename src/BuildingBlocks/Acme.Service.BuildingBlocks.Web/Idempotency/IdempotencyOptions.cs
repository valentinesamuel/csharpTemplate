namespace Acme.Service.BuildingBlocks.Web.Idempotency;

/// <summary>Idempotency-Key settings (config section <c>Idempotency</c>, plan §0.7).</summary>
public sealed class IdempotencyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Idempotency";

    /// <summary>How long a completed response is kept for replay. Default 1 day.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How long the in-progress marker lives if the request never completes. Default 1 minute.</summary>
    public TimeSpan InProgressTtl { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Behaviour when the cache is unavailable. Default <see cref="IdempotencyFailMode.Closed"/>.</summary>
    public IdempotencyFailMode FailMode { get; set; } = IdempotencyFailMode.Closed;
}

/// <summary>What an idempotent endpoint does when the cache is unavailable.</summary>
public enum IdempotencyFailMode
{
    /// <summary>Reject the request with 503 <c>idempotency_unavailable</c>.</summary>
    Closed,

    /// <summary>Process the request without idempotency and log a warning.</summary>
    Open,
}
