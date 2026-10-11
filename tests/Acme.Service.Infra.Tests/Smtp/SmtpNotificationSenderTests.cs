using System.Net.Sockets;
using Acme.Service.BuildingBlocks.Infra.Smtp;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.Infra.Tests.Fixtures;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;
using Shouldly;

namespace Acme.Service.Infra.Tests.Smtp;

public sealed class SmtpNotificationSenderTests
{
    private const string Password = "smtp-sender-secret-0b44";
    private static readonly MailboxAddress _from = new("Service", "no-reply@example.com");

    [Fact]
    public void BuildMessage_TextAndHtml_HasFromToSubjectAndBothParts()
    {
        var mime = SmtpNotificationSender.BuildMessage(
            new EmailMessage("user@example.com", "Subject line", "Plain body", "<p>Html body</p>"), _from);

        mime.From.Mailboxes.ShouldHaveSingleItem().Address.ShouldBe("no-reply@example.com");
        mime.To.Mailboxes.ShouldHaveSingleItem().Address.ShouldBe("user@example.com");
        mime.Subject.ShouldBe("Subject line");
        mime.TextBody.ShouldBe("Plain body");
        mime.HtmlBody.ShouldBe("<p>Html body</p>");
    }

    [Fact]
    public void BuildMessage_TextOnly_HasNoHtmlPart()
    {
        var mime = SmtpNotificationSender.BuildMessage(new EmailMessage("user@example.com", "s", "Plain body"), _from);

        mime.TextBody.ShouldBe("Plain body");
        mime.HtmlBody.ShouldBeNull();
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("user@")]
    [InlineData("Name <>")]
    public void BuildMessage_InvalidRecipient_ThrowsArgumentException(string recipient) =>
        Should.Throw<ArgumentException>(() => SmtpNotificationSender.BuildMessage(new EmailMessage(recipient, "s", "b"), _from));

    [Fact]
    public async Task SendEmailAsync_StartTlsWithCredentials_ConnectsWithStartTlsAuthenticatesSendsAndDisconnects()
    {
        var client = ConnectedClient();
        var sender = Sender(new SmtpOptions { Host = "smtp.example.com", Port = 587, Username = "mailer", Password = Password, From = "no-reply@example.com" }, client);

        await sender.SendEmailAsync(new EmailMessage("user@example.com", "Hello", "Body"), Ct);

        NSubstitute.Received.InOrder(() =>
        {
            client.ConnectAsync("smtp.example.com", 587, SecureSocketOptions.StartTls, Arg.Any<CancellationToken>());
            client.AuthenticateAsync("mailer", Password, Arg.Any<CancellationToken>());
            client.SendAsync(Arg.Is<MimeMessage>(m => m.Subject == "Hello"), Arg.Any<CancellationToken>(), Arg.Any<MailKit.ITransferProgress>());
            client.DisconnectAsync(true, Arg.Any<CancellationToken>());
        });
        client.Timeout.ShouldBe(30_000);
    }

    [Fact]
    public async Task SendEmailAsync_NoCredentialsNoStartTls_ConnectsInClearAndNeverAuthenticates()
    {
        var client = ConnectedClient();
        var sender = Sender(new SmtpOptions { Host = "localhost", Port = 1025, UseStartTls = false, From = "no-reply@example.com" }, client);

        await sender.SendEmailAsync(new EmailMessage("user@example.com", "Hello", "Body"), Ct);

        await client.Received(1).ConnectAsync("localhost", 1025, SecureSocketOptions.None, Arg.Any<CancellationToken>());
        await client.DidNotReceiveWithAnyArgs().AuthenticateAsync(default(string)!, default(string)!, Ct);
    }

    [Fact]
    public async Task SendEmailAsync_SendFails_ThrowsDisconnectsAndLogsNothingSensitive()
    {
        using var logs = new RecordingLoggerProvider();
        var client = ConnectedClient();
        client.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<MailKit.ITransferProgress>())
            .Returns<Task<string>>(_ => throw new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "rejected"));
        var sender = new SmtpNotificationSender(
            Options.Create(new SmtpOptions { Host = "smtp.example.com", Username = "mailer", Password = Password, From = "no-reply@example.com" }),
            logs.CreateLogger<SmtpNotificationSender>(),
            () => client);

        await Should.ThrowAsync<SmtpCommandException>(
            () => sender.SendEmailAsync(new EmailMessage("user@example.com", "Hello", "body-token-91"), Ct));

        await client.Received(1).DisconnectAsync(true, Arg.Any<CancellationToken>());
        logs.Entries.ShouldAllBe(e => !e.Message.Contains(Password) && !e.Message.Contains("body-token-91"));
    }

    [Fact]
    public async Task SendEmailAsync_ServerDown_ThrowsWithinTimeout()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var sender = new SmtpNotificationSender(
            Options.Create(new SmtpOptions { Host = "127.0.0.1", Port = closedPort, UseStartTls = false, From = "no-reply@example.com" }),
            new RecordingLoggerProvider().CreateLogger<SmtpNotificationSender>());
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<SocketException>(
            () => sender.SendEmailAsync(new EmailMessage("user@example.com", "Hello", "Body"), Ct));

        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SendEmailAsync_InvalidRecipient_ThrowsBeforeConnecting()
    {
        var client = ConnectedClient();
        var sender = Sender(new SmtpOptions { Host = "smtp.example.com", From = "no-reply@example.com" }, client);

        await Should.ThrowAsync<ArgumentException>(() => sender.SendEmailAsync(new EmailMessage("nobody", "Hello", "Body"), Ct));

        await client.DidNotReceiveWithAnyArgs().ConnectAsync(default(string)!, default, default(SecureSocketOptions), Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISmtpClient ConnectedClient()
    {
        var client = Substitute.For<ISmtpClient>();
        client.IsConnected.Returns(true);
        return client;
    }

    private static SmtpNotificationSender Sender(SmtpOptions options, ISmtpClient client) =>
        new(Options.Create(options), new RecordingLoggerProvider().CreateLogger<SmtpNotificationSender>(), () => client);
}
