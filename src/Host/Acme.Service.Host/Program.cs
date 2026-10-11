using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Infra.AzureBlob;
using Acme.Service.BuildingBlocks.Infra.Hangfire;
using Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;
using Acme.Service.BuildingBlocks.Infra.Redis;
using Acme.Service.BuildingBlocks.Infra.S3;
using Acme.Service.BuildingBlocks.Infra.Smtp;
using Acme.Service.BuildingBlocks.Persistence.Locking;
using Acme.Service.BuildingBlocks.Web.Correlation;
using Acme.Service.BuildingBlocks.Web.Errors;
using Acme.Service.BuildingBlocks.Web.Idempotency;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Host.Commands;
using Acme.Service.Host.Health;
using Acme.Service.Host.Http;
using Acme.Service.Host.Messaging;
using Acme.Service.Host.Modules;
using Acme.Service.Host.Observability;
using Acme.Service.Host.OpenApi;
using Acme.Service.Host.Providers;
using Acme.Service.Host.Security;
using Microsoft.Extensions.Options;
using Serilog;

// Startup-failure logger only; the configured pipeline logger is built by AddObservability.
// JSON to stdout, synchronous, so a Fatal line is always flushed by CloseAndFlushAsync below (LOG-05).
Log.Logger = LoggingSetup.CreateBootstrapLogger();

try
{
    // Verb dispatch (§0.9). Anything that is not a known verb (including host switches such as
    // --environment passed by WebApplicationFactory) runs the web app.
    return args.FirstOrDefault() switch
    {
        MigrateCommand.Verb => await RunVerbAsync(MigrateCommand.RunAsync, args),
        GrantRoleVerb.Verb => await RunVerbAsync(GrantRoleVerb.RunAsync, args),
        _ => await RunWebAsync(args),
    };
}
catch (Exception ex) when (ex is OptionsValidationException or AggregateException { InnerExceptions: [OptionsValidationException, ..] })
{
    var failures = ex is AggregateException aggregate
        ? aggregate.InnerExceptions.OfType<OptionsValidationException>().SelectMany(e => e.Failures)
        : ((OptionsValidationException)ex).Failures;
    Log.Fatal("Invalid configuration: {Failures}", string.Join(" ", failures));
    return 1;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// One correlation id per verb invocation (S6). Never wraps RunWebAsync: web lines get their id per request.
static async Task<int> RunVerbAsync(Func<string[], Task<int>> verb, string[] args)
{
    using (CliLogContext.BeginInvocation())
    {
        return await verb(args);
    }
}

static async Task<int> RunWebAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddObservability();
    builder.Services.AddForwardedHeadersSetup(builder.Configuration);
    builder.Services.AddCorrelationId();
    builder.Services.AddSingleton<ICorrelationContext, CorrelationContextBridge>();
    builder.Services.AddProblemDetailsSetup();
    builder.Services.AddHealthEndpoints();
    builder.Services.AddOpenApiSetup();
    builder.Services.AddDataProtectionSetup();

    // Infrastructure providers (D-02): one line per provider; <Category>:Provider selects one per category.
    // An invalid selection throws OptionsValidationException here (exit 1, logged above).
    builder.AddProviders(p =>
    {
        p.Add<InMemoryCacheRegistration>();
        p.Add<RedisCacheRegistration>();
        p.Add<LocalBlobRegistration>();
        p.Add<S3BlobRegistration>();
        p.Add<AzureBlobRegistration>();
        p.Add<ConsoleNotificationsRegistration>();
        p.Add<SmtpRegistration>();
        p.Add<InMemoryJobsRegistration>();
        p.Add<HangfireRegistration>();
        p.Add<InMemoryLockRegistration>();
        p.Add<PostgresLockRegistration>();
    });

    // Idempotency-Key filter (P4) for endpoints that opt in with WithIdempotency(); backed by the selected ICache.
    builder.Services.AddIdempotency(builder.Configuration);

    // Modules (§0.6): auth first, then the others. Registration never migrates (INV-16).
    builder.AddModules(HostModules.Create());

    var app = builder.Build();

    // Order matters: forwarded headers first so every later component sees the real client IP/scheme.
    app.UseForwardedHeadersSetup();
    app.UseCorrelationId();
    app.UseRequestLogging();
    app.UseProblemDetailsSetup();

    // Rate limiting (D-12) partitions by client IP, so it runs after forwarded headers (real client address) and after
    // routing (endpoint policies such as the Auth module's auth-credentials and auth-email), before authentication.
    app.UseRouting();
    app.UseRateLimiter();

    // Fail closed (INV-12): JwtBearer is the default scheme and FallbackPolicy requires an authenticated user, so every
    // endpoint (and every unmatched request) needs a valid access token unless it is explicitly .AllowAnonymous() and
    // listed in the anonymous endpoint allowlist test.
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthEndpoints();
    app.MapOpenApiSetup();
    app.MapModuleEndpoints();

    // Only when Jobs:Provider=Hangfire, Jobs:Hangfire:DashboardEnabled=true and the environment is Development (D-21).
    // Anonymous for the fallback policy: Hangfire's own filter still restricts it to local requests (R-24).
    app.MapHangfireDashboardIfEnabled()?.AllowAnonymous();

    await app.RunAsync();
    return 0;
}

public partial class Program;
