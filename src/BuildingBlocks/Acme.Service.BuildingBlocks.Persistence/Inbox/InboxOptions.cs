using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Persistence.Inbox;

/// <summary>Inbox retention settings (config section <c>Inbox</c>, plan §0.7).</summary>
public sealed class InboxOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Inbox";

    /// <summary>Inbox rows older than this are deleted by <c>OutboxCleanup</c>. Default 30.</summary>
    /// <remarks>Must exceed the longest possible redelivery window, or a late redelivery is applied twice.</remarks>
    public int RetentionDays { get; set; } = 30;

    internal List<string> BindingErrors { get; } = [];

    /// <summary>Binds the section and validates it on start (INV-17). Idempotent.</summary>
    internal static void Register(IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IValidateOptions<InboxOptions>)))
        {
            return;
        }

        services.AddOptions<InboxOptions>()
            .Configure(options => Bind(configuration.GetSection(SectionName), options))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InboxOptions>, InboxOptionsValidator>());
    }

    internal static void Bind(IConfigurationSection section, InboxOptions options) =>
        ConfigurationReader.ReadInt32(section, nameof(RetentionDays), v => options.RetentionDays = v, options.BindingErrors);
}

/// <summary>Validates <see cref="InboxOptions"/>; every message names the full configuration key.</summary>
internal sealed class InboxOptionsValidator : IValidateOptions<InboxOptions>
{
    public ValidateOptionsResult Validate(string? name, InboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>(options.BindingErrors);
        if (options.RetentionDays < 1)
        {
            failures.Add($"{InboxOptions.SectionName}:{nameof(InboxOptions.RetentionDays)} must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
