namespace Acme.Service.BuildingBlocks.Notifications;

/// <summary>
/// Provider-neutral notification port (selected by <c>Notifications:Provider</c>). Never called inside a
/// unit-of-work transaction (INV-10); trigger it from a job or event handler instead.
/// </summary>
public interface INotificationSender
{
    /// <summary>Sends <paramref name="message"/>.</summary>
    Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken);
}
