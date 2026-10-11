using Acme.Service.BuildingBlocks.Infra.Smtp;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.Infra.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing.Contracts;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.Infra.Tests.Smtp;

/// <summary>The notification contract against a real SMTP server (Mailpit), checked through the Mailpit API.</summary>
[Trait("Category", "Integration")]
public sealed class SmtpNotificationSenderContractTests(MailpitFixture mailpit)
    : NotificationSenderContractTests, IClassFixture<MailpitFixture>
{
    private const string From = "Service <no-reply@example.com>";
    private static readonly TimeSpan _deliveryTimeout = TimeSpan.FromSeconds(10);
    private readonly RecordingLoggerProvider _logs = new();

    protected override ValueTask<INotificationSender> CreateSenderAsync() =>
        ValueTask.FromResult<INotificationSender>(new SmtpNotificationSender(
            Options.Create(new SmtpOptions
            {
                Host = mailpit.SmtpHost,
                Port = mailpit.SmtpHostPort,
                UseStartTls = false,
                From = From,
            }),
            _logs.CreateLogger<SmtpNotificationSender>()));

    protected override async Task AssertDeliveredAsync(EmailMessage message)
    {
        var received = await mailpit.FindBySubjectAsync(message.Subject, _deliveryTimeout, Ct);

        received.Subject.ShouldBe(message.Subject);
        received.To.ShouldHaveSingleItem().Address.ShouldBe(message.To);
        received.From.ShouldNotBeNull().Address.ShouldBe("no-reply@example.com");
        received.Text.Trim().ShouldBe(message.TextBody);
        if (message.HtmlBody is null)
        {
            received.Html.ShouldBeEmpty();
        }
        else
        {
            received.Html.ShouldContain(message.HtmlBody);
        }
    }

    [Fact]
    public async Task SendEmailAsync_Delivered_LogsRecipientAndSubjectButNoBody()
    {
        var message = new EmailMessage("logged@example.com", NewSubject(), "body-secret-token-5f1");

        await Sender.SendEmailAsync(message, Ct);

        await mailpit.FindBySubjectAsync(message.Subject, _deliveryTimeout, Ct);
        var entry = _logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain(message.To);
        entry.Message.ShouldContain(message.Subject);
        entry.Message.ShouldNotContain(message.TextBody);
    }

    [Fact]
    public async Task SendEmailAsync_StartTlsRequiredButNotOffered_ThrowsAndSendsNothing()
    {
        var sender = new SmtpNotificationSender(
            Options.Create(new SmtpOptions { Host = mailpit.SmtpHost, Port = mailpit.SmtpHostPort, UseStartTls = true, From = From }),
            _logs.CreateLogger<SmtpNotificationSender>());
        var message = new EmailMessage("someone@example.com", NewSubject(), "Never sent in clear text.");

        await Should.ThrowAsync<NotSupportedException>(() => sender.SendEmailAsync(message, Ct));

        await Should.ThrowAsync<TimeoutException>(() => mailpit.FindBySubjectAsync(message.Subject, TimeSpan.FromSeconds(1), Ct));
    }
}
