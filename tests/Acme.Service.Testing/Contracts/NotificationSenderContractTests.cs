using Acme.Service.BuildingBlocks.Notifications;
using Shouldly;
using Xunit;

namespace Acme.Service.Testing.Contracts;

/// <summary>
/// Behaviour every <see cref="INotificationSender"/> provider must have (P7; reused by SMTP in P10). Derive,
/// implement <see cref="CreateSenderAsync"/> and <see cref="AssertDeliveredAsync"/> (for example: a captured log
/// entry, or the Mailpit API), and add the provider's own traits. Subjects are unique per test.
/// </summary>
public abstract class NotificationSenderContractTests : IAsyncLifetime
{
    private INotificationSender? _sender;

    /// <summary>The sender under test (created per test).</summary>
    protected INotificationSender Sender => _sender ?? throw new InvalidOperationException("The sender is not initialized.");

    /// <summary>Creates the sender under test.</summary>
    protected abstract ValueTask<INotificationSender> CreateSenderAsync();

    /// <summary>Asserts that <paramref name="message"/> reached the provider's destination (recipient and subject).</summary>
    protected abstract Task AssertDeliveredAsync(EmailMessage message);

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync() => _sender = await CreateSenderAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        switch (_sender)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SendEmailAsync_TextMessage_IsDelivered()
    {
        var message = new EmailMessage("someone@example.com", NewSubject(), "Hello in plain text.");

        await Sender.SendEmailAsync(message, Ct);

        await AssertDeliveredAsync(message);
    }

    [Fact]
    public async Task SendEmailAsync_WithHtmlAlternative_IsDelivered()
    {
        var message = new EmailMessage("someone@example.com", NewSubject(), "Hello in plain text.", "<p>Hello in <b>HTML</b>.</p>");

        await Sender.SendEmailAsync(message, Ct);

        await AssertDeliveredAsync(message);
    }

    [Fact]
    public async Task SendEmailAsync_NullMessage_ThrowsArgumentNullException() =>
        await Should.ThrowAsync<ArgumentNullException>(() => Sender.SendEmailAsync(null!, Ct));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task SendEmailAsync_BlankRecipient_ThrowsArgumentException(string recipient)
    {
        var exception = await Should.ThrowAsync<Exception>(() => Sender.SendEmailAsync(new EmailMessage(recipient, NewSubject(), "body"), Ct));

        exception.ShouldBeAssignableTo<ArgumentException>();
    }

    /// <summary>The current test's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A subject unique to this call.</summary>
    protected static string NewSubject() => $"Contract test {Guid.NewGuid():N}";
}
