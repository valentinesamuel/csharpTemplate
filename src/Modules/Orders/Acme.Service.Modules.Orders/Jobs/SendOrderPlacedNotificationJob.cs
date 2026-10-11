using System.Globalization;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.Modules.Auth.Contracts;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Orders.Jobs;

/// <summary>Payload of <see cref="SendOrderPlacedNotificationJob"/>: identifiers and amounts only, never an address.</summary>
/// <param name="OrderId">The placed order.</param>
/// <param name="CustomerId">The customer (user id) to notify.</param>
/// <param name="Currency">ISO 4217 currency code of the order.</param>
/// <param name="Total">Order total.</param>
internal sealed record OrderPlacedNotificationPayload(Guid OrderId, Guid CustomerId, string Currency, decimal Total);

/// <summary>
/// Emails the customer that their order was placed. The address is looked up at send time through Auth's
/// <see cref="IUserDirectory"/> (a cross-module call via Contracts, INV-01), so it is never stored in the outbox or the
/// job store. A user that no longer exists (deleted) is skipped with an Information log.
/// </summary>
/// <remarks>
/// Runs outside any transaction (INV-10). A repeated run sends the email again (at-least-once jobs); a sender failure
/// propagates so the job provider retries. The address is never logged (INV-28).
/// </remarks>
internal sealed partial class SendOrderPlacedNotificationJob(
    IUserDirectory userDirectory,
    INotificationSender notificationSender,
    ILogger<SendOrderPlacedNotificationJob> logger) : IJob<OrderPlacedNotificationPayload>
{
    public async Task ExecuteAsync(OrderPlacedNotificationPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var user = await userDirectory.GetUserSummaryAsync(payload.CustomerId, cancellationToken).ConfigureAwait(false);
        if (user is null || string.IsNullOrWhiteSpace(user.Email))
        {
            LogSkipped(logger, payload.OrderId, payload.CustomerId);
            return;
        }

        await notificationSender.SendEmailAsync(Compose(user, payload), cancellationToken).ConfigureAwait(false);
        LogSent(logger, payload.OrderId, payload.CustomerId);
    }

    /// <summary>The order confirmation email to <paramref name="user"/>.</summary>
    internal static EmailMessage Compose(UserSummary user, OrderPlacedNotificationPayload payload)
    {
        var greeting = string.IsNullOrWhiteSpace(user.DisplayName) ? "Hello," : $"Hello {user.DisplayName},";
        var total = string.Create(CultureInfo.InvariantCulture, $"{payload.Total:0.00} {payload.Currency}");
        var body = string.Join(
            "\n",
            greeting,
            string.Empty,
            $"We received your order {payload.OrderId}.",
            $"Total: {total}.",
            string.Empty,
            "We will let you know when it is confirmed.");
        return new EmailMessage(user.Email, $"Your order {payload.OrderId} was placed", body);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order placed email for order {OrderId} skipped: user {UserId} does not exist")]
    private static partial void LogSkipped(ILogger logger, Guid orderId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order placed email for order {OrderId} sent to user {UserId}")]
    private static partial void LogSent(ILogger logger, Guid orderId, Guid userId);
}
