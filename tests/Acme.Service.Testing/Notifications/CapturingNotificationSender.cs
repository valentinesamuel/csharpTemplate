using System.Collections.Concurrent;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.Testing.Notifications;

/// <summary>
/// A test <see cref="INotificationSender"/> that records every email instead of sending it, so tests can read the
/// links a flow emails (for example the confirmation link). Thread-safe.
/// </summary>
public sealed class CapturingNotificationSender : INotificationSender
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    /// <summary>Every email sent so far, in order.</summary>
    public IReadOnlyList<EmailMessage> Messages => [.. _messages];

    /// <inheritdoc />
    public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>The emails sent to <paramref name="recipient"/> (case-insensitive).</summary>
    public IReadOnlyList<EmailMessage> SentTo(string recipient) =>
        [.. _messages.Where(m => string.Equals(m.To, recipient, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Waits until <paramref name="count"/> emails have been sent to <paramref name="recipient"/> and returns the
    /// latest one. Polls, because emails arrive asynchronously (outbox, then a job).
    /// </summary>
    /// <exception cref="TimeoutException">Fewer emails arrived within <paramref name="timeout"/>.</exception>
    public async Task<EmailMessage> WaitForEmailAsync(string recipient, TimeSpan timeout, CancellationToken cancellationToken, int count = 1)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var sent = SentTo(recipient);
                if (sent.Count >= count)
                {
                    return sent[^1];
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Expected {count} email(s) to {recipient} within {timeout}; {SentTo(recipient).Count} arrived.");
        }
    }
}

/// <summary>
/// Test provider registration <c>Notifications:Provider=Capture</c>: registers one <see cref="CapturingNotificationSender"/>
/// as the <see cref="INotificationSender"/>, replacing any provider registered before it. Test hosts apply it in
/// <c>ConfigureTestServices</c> (the Host's provider list is fixed in Program).
/// </summary>
public sealed class CapturingNotificationsRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "Capture";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Notifications;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<CapturingNotificationSender>();
        services.Replace(ServiceDescriptor.Singleton<INotificationSender>(sp => sp.GetRequiredService<CapturingNotificationSender>()));
    }
}
