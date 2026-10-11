using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.Modules.Auth.Notifications;

/// <summary>
/// Turns a <see cref="PasswordResetRequested"/> into a <see cref="SendPasswordResetJob"/>. Runs through the inbox
/// decorator inside the Auth unit of work (INV-09); the email itself is sent by the job, outside any transaction
/// (INV-10).
/// </summary>
internal sealed class PasswordResetRequestedHandler(IJobQueue jobQueue) : IIntegrationEventHandler<PasswordResetRequested>
{
    /// <summary>The stable consumer name recorded in <c>auth.inbox_messages</c>.</summary>
    public const string ConsumerName = "auth.send-password-reset";

    public Task HandleAsync(PasswordResetRequested integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return jobQueue.EnqueueAsync<SendPasswordResetJob, SendPasswordResetPayload>(
            new SendPasswordResetPayload(integrationEvent.UserId),
            cancellationToken);
    }
}
