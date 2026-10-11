using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Orders.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Unit;

public sealed class SendOrderPlacedNotificationJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Execute_ExistingUser_EmailsTheDirectoryAddress()
    {
        var payload = new OrderPlacedNotificationPayload(Guid.CreateVersion7(), Guid.CreateVersion7(), "EUR", 12.5m);
        var directory = Substitute.For<IUserDirectory>();
        directory.GetUserSummaryAsync(payload.CustomerId, Arg.Any<CancellationToken>())
            .Returns(new UserSummary(payload.CustomerId, "buyer@example.com", "Ada", IsActive: true));
        var sender = Substitute.For<INotificationSender>();

        await new SendOrderPlacedNotificationJob(directory, sender, NullLogger<SendOrderPlacedNotificationJob>.Instance).ExecuteAsync(payload, Ct);

        await sender.Received(1).SendEmailAsync(
            Arg.Is<EmailMessage>(m => m.To == "buyer@example.com"
                && m.Subject.Contains(payload.OrderId.ToString(), StringComparison.Ordinal)
                && m.TextBody.Contains("12.50 EUR", StringComparison.Ordinal)
                && m.TextBody.StartsWith("Hello Ada,", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_DeletedUser_SkipsTheEmail()
    {
        var payload = new OrderPlacedNotificationPayload(Guid.CreateVersion7(), Guid.CreateVersion7(), "EUR", 1m);
        var directory = Substitute.For<IUserDirectory>();
        directory.GetUserSummaryAsync(payload.CustomerId, Arg.Any<CancellationToken>()).Returns((UserSummary?)null);
        var sender = Substitute.For<INotificationSender>();

        await new SendOrderPlacedNotificationJob(directory, sender, NullLogger<SendOrderPlacedNotificationJob>.Instance).ExecuteAsync(payload, Ct);

        await sender.DidNotReceiveWithAnyArgs().SendEmailAsync(default!, Ct);
    }
}
