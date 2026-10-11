using Acme.Service.BuildingBlocks.Infra.InMemory;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.Infra.Tests.Fixtures;
using Acme.Service.Testing.Contracts;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace Acme.Service.Infra.Tests.InMemory;

/// <summary>Contract run outside Development: recipient and subject are logged, never the body (INV-28).</summary>
public sealed class ConsoleNotificationSenderContractTests : NotificationSenderContractTests
{
    private readonly RecordingLoggerProvider _logs = new();

    protected override ValueTask<INotificationSender> CreateSenderAsync() =>
        ValueTask.FromResult<INotificationSender>(
            new ConsoleNotificationSender(Environment(Environments.Production), _logs.CreateLogger<ConsoleNotificationSender>()));

    protected override Task AssertDeliveredAsync(EmailMessage message)
    {
        var entry = _logs.Entries.Single(e => e.Message.Contains(message.Subject, StringComparison.Ordinal));
        entry.Message.ShouldContain(message.To);
        entry.Message.ShouldNotContain(message.TextBody);
        if (message.HtmlBody is not null)
        {
            entry.Message.ShouldNotContain(message.HtmlBody);
        }

        return Task.CompletedTask;
    }

    internal static IHostEnvironment Environment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }
}

public sealed class ConsoleNotificationSenderTests
{
    [Fact]
    public async Task SendEmailAsync_Development_LogsBody()
    {
        using var logs = new RecordingLoggerProvider();
        var sender = new ConsoleNotificationSender(
            ConsoleNotificationSenderContractTests.Environment(Environments.Development),
            logs.CreateLogger<ConsoleNotificationSender>());

        await sender.SendEmailAsync(new EmailMessage("dev@example.com", "Dev subject", "Dev body text"), TestContext.Current.CancellationToken);

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain("dev@example.com");
        entry.Message.ShouldContain("Dev subject");
        entry.Message.ShouldContain("Dev body text");
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task SendEmailAsync_NotDevelopment_OmitsBody(string environmentName)
    {
        using var logs = new RecordingLoggerProvider();
        var sender = new ConsoleNotificationSender(
            ConsoleNotificationSenderContractTests.Environment(environmentName),
            logs.CreateLogger<ConsoleNotificationSender>());

        await sender.SendEmailAsync(
            new EmailMessage("user@example.com", "Reset your password", "token=SECRET-TOKEN", "<a>SECRET-TOKEN</a>"),
            TestContext.Current.CancellationToken);

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain("Reset your password");
        entry.Message.ShouldNotContain("SECRET-TOKEN");
    }
}
