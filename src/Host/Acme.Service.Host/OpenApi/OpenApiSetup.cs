using Scalar.AspNetCore;

namespace Acme.Service.Host.OpenApi;

/// <summary>
/// Built-in OpenAPI document (<c>/openapi/v1.json</c>) and the Scalar UI (<c>/scalar</c>), mapped only
/// when <c>OpenApi:Enabled</c> is true (D-24), with the bearer security scheme. Both are anonymous (they are listed in
/// the anonymous endpoint allowlist) because they are enabled only in Development.
/// </summary>
internal static class OpenApiSetup
{
    public const string EnabledKey = "OpenApi:Enabled";

    public static IServiceCollection AddOpenApiSetup(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
            options.AddOperationTransformer<BearerSecuritySchemeTransformer>();
        });
        return services;
    }

    public static WebApplication MapOpenApiSetup(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>(EnabledKey))
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }

        return app;
    }
}
