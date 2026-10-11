using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.Modules.Auth.Notifications;

/// <summary>
/// Turns an <see cref="EmailVerificationRequested"/> into a <see cref="SendEmailVerificationJob"/>. Runs through the
/// inbox decorator inside the Auth unit of work (INV-09); the email itself is sent by the job, outside any
/// transaction (INV-10).
/// </summary>
internal sealed class EmailVerificationRequestedHandler(IJobQueue jobQueue) : IIntegrationEventHandler<EmailVerificationRequested>
{
    /// <summary>The stable consumer name recorded in <c>auth.inbox_messages</c>.</summary>
    public const string ConsumerName = "auth.send-email-verification";

    public Task HandleAsync(EmailVerificationRequested integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return jobQueue.EnqueueAsync<SendEmailVerificationJob, SendEmailVerificationPayload>(
            new SendEmailVerificationPayload(integrationEvent.UserId),
            cancellationToken);
    }
}
