using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Orders.Options;

/// <summary>
/// <c>Orders</c>: how long an order may stay pending before the expiry job expires it, and the attachment upload limit.
/// Validated on start (INV-17).
/// </summary>
internal sealed class OrdersOptions
{
    public const string SectionName = "Orders";

    /// <summary>Default <see cref="PendingExpiry"/>: 24 hours.</summary>
    public static readonly TimeSpan DefaultPendingExpiry = TimeSpan.FromHours(24);

    /// <summary>
    /// <c>Orders:PendingExpiry</c>: a pending order placed longer ago than this is expired by
    /// <c>ExpireStalePendingOrdersJob</c>. Default 24 hours.
    /// </summary>
    public TimeSpan PendingExpiry { get; set; } = DefaultPendingExpiry;

    /// <summary><c>Orders:Attachments</c>.</summary>
    public AttachmentOptions Attachments { get; set; } = new();

    /// <summary>Registers the options with key-named validation and <c>ValidateOnStart</c>.</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OrdersOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(o => o.PendingExpiry > TimeSpan.Zero, $"{SectionName}:{nameof(PendingExpiry)} must be greater than zero.")
            .Validate(
                o => o.Attachments is { MaxBytes: > 0 },
                $"{SectionName}:{nameof(Attachments)}:{nameof(AttachmentOptions.MaxBytes)} must be greater than zero.")
            .ValidateOnStart();
    }

    /// <summary><c>Orders:Attachments</c>: order attachment uploads.</summary>
    internal sealed class AttachmentOptions
    {
        /// <summary>Default <see cref="MaxBytes"/>: 10 MiB.</summary>
        public const long DefaultMaxBytes = 10L * 1024 * 1024;

        /// <summary><c>Orders:Attachments:MaxBytes</c>: largest accepted file; a larger upload is 413. Default 10 MiB.</summary>
        public long MaxBytes { get; set; } = DefaultMaxBytes;
    }
}
