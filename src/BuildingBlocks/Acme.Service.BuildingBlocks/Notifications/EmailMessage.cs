namespace Acme.Service.BuildingBlocks.Notifications;

/// <summary>An outbound email. The sender address comes from provider configuration.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="TextBody">Plain-text body (always required).</param>
/// <param name="HtmlBody">Optional HTML alternative.</param>
public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);
