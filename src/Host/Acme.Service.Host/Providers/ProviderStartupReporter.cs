using Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;
using Acme.Service.BuildingBlocks.Providers;
using Acme.Service.BuildingBlocks.Web.Idempotency;
using Microsoft.Extensions.Options;

namespace Acme.Service.Host.Providers;

/// <summary>
/// Logs the selected provider per category when the host starts and, per D-21, warns when
/// <c>Cache:Provider=InMemory</c> backs idempotency: the cache is per replica, so Idempotency-Key replay and
/// in-progress detection do not hold across replicas.
/// </summary>
internal sealed partial class ProviderStartupReporter(
    ProviderSelectionReport report,
    IServiceProviderIsService serviceCheck,
    ILogger<ProviderStartupReporter> logger) : IHostedService
{
    /// <summary>
    /// True when idempotency is enabled, that is when <c>AddIdempotency</c> registered its validated options.
    /// </summary>
    internal static bool IsIdempotencyEnabled(IServiceProviderIsService serviceCheck) =>
        serviceCheck.IsService(typeof(IValidateOptions<IdempotencyOptions>));

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var selection in report.Selections)
        {
            LogSelected(logger, selection.Category, selection.Registration.Name);
        }

        var cache = report.Selections.FirstOrDefault(s => s.Category == ProviderCategory.Cache);
        if (cache is not null
            && string.Equals(cache.Registration.Name, InMemoryCacheRegistration.ProviderName, StringComparison.OrdinalIgnoreCase)
            && IsIdempotencyEnabled(serviceCheck))
        {
            LogInMemoryCacheWithIdempotency(logger);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Provider {ProviderCategory}:Provider = {Provider}")]
    private static partial void LogSelected(ILogger logger, ProviderCategory providerCategory, string provider);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Cache:Provider=InMemory with idempotency enabled: Idempotency-Key state is per replica and lost on restart; use a distributed cache when running more than one replica (D-21)")]
    private static partial void LogInMemoryCacheWithIdempotency(ILogger logger);
}
