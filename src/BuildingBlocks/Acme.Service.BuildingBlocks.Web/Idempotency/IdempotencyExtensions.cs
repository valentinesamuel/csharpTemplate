using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.BuildingBlocks.Web.Idempotency;

/// <summary>Registration and opt-in of the Idempotency-Key endpoint filter.</summary>
public static class IdempotencyExtensions
{
    /// <summary>
    /// Binds and validates <see cref="IdempotencyOptions"/> (validated on start, INV-17) and registers the filter.
    /// Requires an <see cref="Caching.ICache"/> registration.
    /// </summary>
    public static IServiceCollection AddIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .Validate(o => o.Ttl > TimeSpan.Zero, "Idempotency:Ttl must be greater than zero.")
            .Validate(o => o.InProgressTtl > TimeSpan.Zero, "Idempotency:InProgressTtl must be greater than zero.")
            .Validate(o => o.InProgressTtl <= o.Ttl, "Idempotency:InProgressTtl must not exceed Idempotency:Ttl.")
            .Validate(o => Enum.IsDefined(o.FailMode), "Idempotency:FailMode must be Closed or Open.")
            .ValidateOnStart();

        services.TryAddTransient<IdempotencyFilter>();
        return services;
    }

    /// <summary>
    /// Opts the endpoint (or group) into Idempotency-Key handling. Requests without the header are processed
    /// normally. Requires <see cref="AddIdempotency"/>.
    /// </summary>
    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The body must be re-readable for hashing after parameter binding has consumed it, so buffering is
        // enabled before the handler (and its binding) runs.
        builder.Add(endpoint =>
        {
            var inner = endpoint.RequestDelegate;
            if (inner is not null)
            {
                endpoint.RequestDelegate = context =>
                {
                    context.Request.EnableBuffering();
                    return inner(context);
                };
            }
        });

        builder.AddEndpointFilter(static (context, next) =>
        {
            var filter = context.HttpContext.RequestServices.GetService<IdempotencyFilter>()
                ?? throw new InvalidOperationException("WithIdempotency requires services.AddIdempotency(configuration).");
            return filter.InvokeAsync(context, next);
        });

        return builder;
    }
}
