using System.Globalization;
using System.Threading.RateLimiting;
using Acme.Service.BuildingBlocks.Web.Errors;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Acme.Service.Modules.Auth.RateLimiting;

/// <summary>
/// Rate limiting of the anonymous account endpoints with the built-in ASP.NET Core limiter (D-12), in process and
/// per replica, on top of Identity lockout:
/// <list type="bullet">
/// <item><c>auth-credentials</c> (login, refresh): a sliding window per client IP.</item>
/// <item><c>auth-email</c> (register, resend-confirmation): chained limits, a sliding window per client IP (the
/// <see cref="EmailPolicy"/> policy of the rate limiting middleware) and then a sliding window per normalized email
/// address (<see cref="EmailAddressRateLimitFilter"/>), so neither one address can be flooded from many clients nor
/// many addresses from one client.</item>
/// </list>
/// The client IP is the connection's remote address after the forwarded headers middleware: the Host runs
/// <c>UseRateLimiter</c> after <c>UseForwardedHeaders</c> and <c>UseRouting</c>. Behind a proxy that is not trusted
/// (Http:ForwardedHeaders), every client shares the proxy's address (P2 logs a warning). Rejections are 429 ProblemDetails
/// (<c>too_many_requests</c>) with <c>Retry-After</c> in seconds (INV-18).
/// </summary>
/// <remarks>
/// The email is taken from the bound request body (the JSON body is already deserialized, at most
/// <see cref="MaxEmailRequestBodyBytes"/> bytes) in an endpoint filter, because the middleware's partition callback is
/// synchronous and cannot read the request body.
/// </remarks>
internal static partial class AuthRateLimitingSetup
{
    /// <summary>Per-IP policy of login and refresh.</summary>
    public const string CredentialsPolicy = "auth-credentials";

    /// <summary>Per-IP (and, with the endpoint filter, per-email) policy of register and resend-confirmation.</summary>
    public const string EmailPolicy = "auth-email";

    /// <summary>The <c>code</c> of a rejected request.</summary>
    public const string TooManyRequestsCode = "too_many_requests";

    /// <summary>Request body limit of the endpoints limited per email.</summary>
    public const long MaxEmailRequestBodyBytes = 4 * 1024;

    /// <summary>Partition of requests without a known client address (all such requests share it).</summary>
    public const string UnknownClient = "unknown";

    /// <summary><c>EventType</c> of the security event logged for each rejected request.</summary>
    public const string RejectedEventType = "RateLimitRejected";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        AuthRateLimitingOptions.Register(services, configuration);
        services.AddSingleton<EmailAddressRateLimiter>();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy<string, CredentialsRateLimiterPolicy>(CredentialsPolicy);
            options.AddPolicy<string, EmailRateLimiterPolicy>(EmailPolicy);
        });

        return services;
    }

    /// <summary>Applies the <c>auth-credentials</c> policy.</summary>
    public static RouteHandlerBuilder RequireCredentialsRateLimiting(this RouteHandlerBuilder builder) =>
        builder.RequireRateLimiting(CredentialsPolicy).ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// Applies the chained <c>auth-email</c> limits: the per-IP policy and the per-email filter. The endpoint must bind
    /// a body implementing <see cref="IEmailRateLimited"/>; its size is capped at <see cref="MaxEmailRequestBodyBytes"/>.
    /// </summary>
    public static RouteHandlerBuilder RequireEmailRateLimiting(this RouteHandlerBuilder builder) =>
        builder.RequireRateLimiting(EmailPolicy)
            .AddEndpointFilter<EmailAddressRateLimitFilter>()
            .WithMetadata(new RequestBodySizeLimit(MaxEmailRequestBodyBytes))
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    /// <summary>Partition key of the client: its IP address, or <see cref="UnknownClient"/>.</summary>
    public static string ClientPartition(HttpContext context) => AccountHttp.ClientAddress(context) ?? UnknownClient;

    /// <summary>
    /// The wait announced in <c>Retry-After</c>: the limiter's own hint when it gives one, otherwise one window segment
    /// (the longest wait until a permit returns), in whole seconds and at least 1.
    /// </summary>
    public static int RetryAfterSeconds(RateLimitLease lease, AuthRateLimitingOptions.WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(settings);

        var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var hint) && hint > TimeSpan.Zero ? hint : settings.SegmentLength;
        return (int)Math.Max(1, Math.Ceiling(wait.TotalSeconds));
    }

    /// <summary>Writes the 429 ProblemDetails response with <c>Retry-After</c> and logs the rejection (no identifiers).</summary>
    public static async ValueTask RejectAsync(HttpContext context, string policy, int retryAfterSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AuthRateLimitingSetup));
        LogRejected(logger, RejectedEventType, policy, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers[HeaderNames.RetryAfter] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Detail = "Too many requests. Try again later.",
        };
        problem.Extensions[ProblemDetailsSetup.CodeKey] = TooManyRequestsCode;

        var problemDetails = context.RequestServices.GetService<IProblemDetailsService>();
        if (problemDetails is null
            || !await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }).ConfigureAwait(false))
        {
            await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SecurityEvent {EventType}: rate limit {Policy} rejected a request to {Path}")]
    private static partial void LogRejected(ILogger logger, string eventType, string policy, PathString path);

    /// <summary>Caps the request body of an endpoint (honoured by servers that support it, such as Kestrel).</summary>
    private sealed class RequestBodySizeLimit(long maxBytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => maxBytes;
    }
}

/// <summary>A request body whose email address is rate limited per address (<c>auth-email</c>).</summary>
internal interface IEmailRateLimited
{
    /// <summary>The email address as sent by the client.</summary>
    string? Email { get; }
}

/// <summary>A sliding window per client IP, from one <see cref="AuthRateLimitingOptions.WindowSettings"/>.</summary>
internal abstract class ClientIpRateLimiterPolicy(string policyName, AuthRateLimitingOptions.WindowSettings settings) : IRateLimiterPolicy<string>
{
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected =>
        (context, cancellationToken) => AuthRateLimitingSetup.RejectAsync(
            context.HttpContext,
            policyName,
            AuthRateLimitingSetup.RetryAfterSeconds(context.Lease, settings),
            cancellationToken);

    public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
        RateLimitPartition.GetSlidingWindowLimiter(AuthRateLimitingSetup.ClientPartition(httpContext), _ => settings.ToLimiterOptions());
}

/// <summary><c>auth-credentials</c>: per client IP.</summary>
internal sealed class CredentialsRateLimiterPolicy(IOptions<AuthRateLimitingOptions> options)
    : ClientIpRateLimiterPolicy(AuthRateLimitingSetup.CredentialsPolicy, options.Value.Credentials);

/// <summary><c>auth-email</c>, first link of the chain: per client IP.</summary>
internal sealed class EmailRateLimiterPolicy(IOptions<AuthRateLimitingOptions> options)
    : ClientIpRateLimiterPolicy(AuthRateLimitingSetup.EmailPolicy, options.Value.Email);

/// <summary>
/// <c>auth-email</c>, second link of the chain: one sliding window per normalized email address (Identity's lookup
/// normalizer, so case variants share it). Singleton; disposed with the host.
/// </summary>
internal sealed class EmailAddressRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public EmailAddressRateLimiter(IOptions<AuthRateLimitingOptions> options)
    {
        Settings = options.Value.Email;
        var limiterOptions = Settings.ToLimiterOptions();
        _limiter = PartitionedRateLimiter.Create<string, string>(
            email => RateLimitPartition.GetSlidingWindowLimiter(email, _ => limiterOptions),
            StringComparer.Ordinal);
    }

    public AuthRateLimitingOptions.WindowSettings Settings { get; }

    /// <summary>Takes one permit for <paramref name="normalizedEmail"/>; dispose the lease.</summary>
    public RateLimitLease Acquire(string normalizedEmail) => _limiter.AttemptAcquire(normalizedEmail);

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Rejects a request with 429 when the per-email window of its <see cref="IEmailRateLimited"/> body is exhausted. A
/// missing or blank email is left to validation (400).
/// </summary>
internal sealed class EmailAddressRateLimitFilter(EmailAddressRateLimiter limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var email = context.Arguments.OfType<IEmailRateLimited>().FirstOrDefault()?.Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return await next(context).ConfigureAwait(false);
        }

        // The normalizer is scoped (Identity); resolve it from the request, not from the filter's root provider.
        var normalizer = context.HttpContext.RequestServices.GetRequiredService<ILookupNormalizer>();
        var key = normalizer.NormalizeEmail(email) ?? email;
        using var lease = limiter.Acquire(key);
        if (lease.IsAcquired)
        {
            return await next(context).ConfigureAwait(false);
        }

        await AuthRateLimitingSetup.RejectAsync(
            context.HttpContext,
            AuthRateLimitingSetup.EmailPolicy,
            AuthRateLimitingSetup.RetryAfterSeconds(lease, limiter.Settings),
            context.HttpContext.RequestAborted).ConfigureAwait(false);
        return Results.Empty;
    }
}
