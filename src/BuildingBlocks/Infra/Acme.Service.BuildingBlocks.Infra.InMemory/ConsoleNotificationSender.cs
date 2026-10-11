using Acme.Service.BuildingBlocks.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Infra.InMemory;

/// <summary>
/// Development <see cref="INotificationSender"/> (<c>Notifications:Provider=Console</c>, rejected in Production by
/// D-21). Nothing is sent: the recipient and subject are logged, and the bodies only in the Development
/// environment, because they may carry one-time links or codes (INV-28).
/// </summary>
internal sealed partial class ConsoleNotificationSender(
    IHostEnvironment environment,
    ILogger<ConsoleNotificationSender> logger) : INotificationSender
{
    /// <inheritdoc />
    public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.To, nameof(message));
        ArgumentNullException.ThrowIfNull(message.Subject, nameof(message));
        ArgumentNullException.ThrowIfNull(message.TextBody, nameof(message));
        cancellationToken.ThrowIfCancellationRequested();

        if (environment.IsDevelopment())
        {
            LogEmailWithBody(logger, message.To, message.Subject, message.TextBody, message.HtmlBody is not null);
        }
        else
        {
            LogEmail(logger, message.To, message.Subject);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email (not sent; console provider) to {Recipient} with subject {Subject}")]
    private static partial void LogEmail(ILogger logger, string recipient, string subject);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Email (not sent; console provider) to {Recipient} with subject {Subject}, HTML alternative {HasHtmlBody}, text body:\n{TextBody}")]
    private static partial void LogEmailWithBody(ILogger logger, string recipient, string subject, string textBody, bool hasHtmlBody);
}
