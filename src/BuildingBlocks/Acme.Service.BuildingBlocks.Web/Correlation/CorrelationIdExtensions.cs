using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.BuildingBlocks.Web.Correlation;

/// <summary>Registration helpers for correlation id propagation.</summary>
public static class CorrelationIdExtensions
{
    /// <summary>Registers the accessor and forwards the correlation id on every <see cref="HttpClient"/> built by the factory.</summary>
    public static IServiceCollection AddCorrelationId(this IServiceCollection services)
    {
        services.TryAddSingleton<ICorrelationIdAccessor, CorrelationIdAccessor>();
        services.TryAddTransient<CorrelationIdDelegatingHandler>();
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<CorrelationIdDelegatingHandler>());
        return services;
    }

    /// <summary>Adds <see cref="CorrelationIdMiddleware"/> to the pipeline.</summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
