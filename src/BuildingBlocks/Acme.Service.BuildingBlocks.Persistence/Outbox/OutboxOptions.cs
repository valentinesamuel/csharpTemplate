using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Persistence.Outbox;

/// <summary>Outbox dispatch and retention settings (config section <c>Outbox</c>, plan §0.7).</summary>
public sealed class OutboxOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Outbox";

    /// <summary>Upper bound of <see cref="BatchSize"/>.</summary>
    public const int MaxBatchSize = 1000;

    /// <summary>Delay between polls when the previous batch was not full. Default 2 seconds.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Messages locked and dispatched per transaction. Default 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Failed attempts after which a message is marked dead. Default 10.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Processed messages older than this are deleted by <see cref="OutboxCleanup"/>. Default 7.</summary>
    public int RetentionDays { get; set; } = 7;

    internal List<string> BindingErrors { get; } = [];

    /// <summary>Binds the section and validates it on start (INV-17). Idempotent.</summary>
    internal static void Register(IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IValidateOptions<OutboxOptions>)))
        {
            return;
        }

        services.AddOptions<OutboxOptions>()
            .Configure(options => Bind(configuration.GetSection(SectionName), options))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OutboxOptions>, OutboxOptionsValidator>());
    }

    internal static void Bind(IConfigurationSection section, OutboxOptions options)
    {
        ConfigurationReader.ReadTimeSpan(section, nameof(PollingInterval), v => options.PollingInterval = v, options.BindingErrors);
        ConfigurationReader.ReadInt32(section, nameof(BatchSize), v => options.BatchSize = v, options.BindingErrors);
        ConfigurationReader.ReadInt32(section, nameof(MaxAttempts), v => options.MaxAttempts = v, options.BindingErrors);
        ConfigurationReader.ReadInt32(section, nameof(RetentionDays), v => options.RetentionDays = v, options.BindingErrors);
    }
}

/// <summary>Validates <see cref="OutboxOptions"/>; every message names the full configuration key.</summary>
internal sealed class OutboxOptionsValidator : IValidateOptions<OutboxOptions>
{
    private const string Prefix = OutboxOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>(options.BindingErrors);
        if (options.PollingInterval <= TimeSpan.Zero)
        {
            failures.Add($"{Prefix}{nameof(OutboxOptions.PollingInterval)} must be greater than zero.");
        }

        if (options.BatchSize is < 1 or > OutboxOptions.MaxBatchSize)
        {
            failures.Add($"{Prefix}{nameof(OutboxOptions.BatchSize)} must be between 1 and {OutboxOptions.MaxBatchSize}.");
        }

        if (options.MaxAttempts < 1)
        {
            failures.Add($"{Prefix}{nameof(OutboxOptions.MaxAttempts)} must be at least 1.");
        }

        if (options.RetentionDays < 1)
        {
            failures.Add($"{Prefix}{nameof(OutboxOptions.RetentionDays)} must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
