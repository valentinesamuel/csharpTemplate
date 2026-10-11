using Acme.Service.BuildingBlocks.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Acme.Service.BuildingBlocks.Infra.Smtp;

/// <summary>
/// <see cref="INotificationSender"/> over SMTP with MailKit (<c>Notifications:Provider=Smtp</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each send opens its own connection (MailKit clients are not thread-safe), upgrades it with STARTTLS when
/// <see cref="SmtpOptions.UseStartTls"/> is set (failing when the server does not offer it), authenticates when
/// credentials are configured, sends, and disconnects.</item>
/// <item>Every network step is bounded by <see cref="SmtpOptions.Timeout"/> (30 s).</item>
/// <item>Failures (server down, rejected recipient, timeout) are thrown to the caller. Call this only from a job or an
/// integration event handler, never inside a unit-of-work transaction (INV-10), so the job provider retries the send
/// while the outbox still holds the source event.</item>
/// <item>Logs carry the recipient and subject only; bodies and credentials are never logged (INV-28).</item>
/// </list>
/// </remarks>
internal sealed partial class SmtpNotificationSender : INotificationSender
{
    private readonly SmtpOptions _options;
    private readonly string _host;
    private readonly MailboxAddress _from;
    private readonly Func<ISmtpClient> _clientFactory;
    private readonly ILogger<SmtpNotificationSender> _logger;

    public SmtpNotificationSender(IOptions<SmtpOptions> options, ILogger<SmtpNotificationSender> logger)
        : this(options, logger, static () => new SmtpClient())
    {
    }

    internal SmtpNotificationSender(IOptions<SmtpOptions> options, ILogger<SmtpNotificationSender> logger, Func<ISmtpClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clientFactory);

        _options = options.Value;
        _host = _options.Host ?? throw new InvalidOperationException($"{SmtpOptions.HostKey} is not configured.");
        _from = _options.FromAddress
            ?? throw new InvalidOperationException($"{SmtpOptions.FromKey} is not a valid mailbox.");
        _logger = logger;
        _clientFactory = clientFactory;
    }

    /// <inheritdoc />
    public async Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = BuildMessage(message, _from);
        cancellationToken.ThrowIfCancellationRequested();

        using var client = _clientFactory();
        client.Timeout = (int)Math.Ceiling(_options.Timeout.TotalMilliseconds);

        var security = _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(_host, _options.Port, security, cancellationToken).ConfigureAwait(false);
        try
        {
            if (_options is { Username: { } username, Password: { } password })
            {
                await client.AuthenticateAsync(username, password, cancellationToken).ConfigureAwait(false);
            }

            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisconnectQuietlyAsync(client).ConfigureAwait(false);
        }

        LogSent(_logger, message.To, message.Subject);
    }

    /// <summary>Validates <paramref name="message"/> and builds the MIME message (text, plus an HTML alternative when given).</summary>
    internal static MimeMessage BuildMessage(EmailMessage message, MailboxAddress from)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.To, nameof(message));
        ArgumentNullException.ThrowIfNull(message.Subject, nameof(message));
        ArgumentNullException.ThrowIfNull(message.TextBody, nameof(message));

        if (!MailboxAddress.TryParse(ParserOptions.Default, message.To.Trim(), out var to) || !SmtpOptions.IsAddress(to))
        {
            throw new ArgumentException("The recipient is not a valid email address.", nameof(message));
        }

        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        var mime = new MimeMessage { Subject = message.Subject, Body = body.ToMessageBody() };
        mime.From.Add(from);
        mime.To.Add(to);
        return mime;
    }

    private async Task DisconnectQuietlyAsync(ISmtpClient client)
    {
        if (!client.IsConnected)
        {
            return;
        }

        try
        {
            // The message is already sent (or the send failed); a slow QUIT must not hold the job.
            using var quit = new CancellationTokenSource(_options.Timeout);
            await client.DisconnectAsync(quit: true, quit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogDisconnectFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email sent over SMTP to {Recipient} with subject {Subject}")]
    private static partial void LogSent(ILogger logger, string recipient, string subject);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SMTP disconnect failed after the send; ignored")]
    private static partial void LogDisconnectFailed(ILogger logger, Exception exception);
}
