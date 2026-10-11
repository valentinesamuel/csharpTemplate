using System.Globalization;
using System.Text.Encodings.Web;
using Acme.Service.BuildingBlocks.Notifications;
using Microsoft.AspNetCore.WebUtilities;

namespace Acme.Service.Modules.Auth.Notifications;

/// <summary>
/// The Auth module's emails: plain text plus an HTML alternative. Every value placed in HTML is encoded. The bodies
/// contain one-time links, so they are never logged by the module (INV-28); the console provider prints them in
/// Development only.
/// </summary>
internal static class EmailTemplates
{
    public const string EmailVerificationSubject = "Confirm your email address";

    public const string PasswordResetSubject = "Reset your password";

    /// <summary>The query parameter carrying the user id in emailed links.</summary>
    public const string UserIdParameter = "userId";

    /// <summary>The query parameter carrying the one-time token in emailed links.</summary>
    public const string TokenParameter = "token";

    /// <summary>The email asking <paramref name="to"/> to confirm the address by opening <paramref name="link"/>.</summary>
    public static EmailMessage EmailVerification(string to, string link, TimeSpan lifespan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(link);

        var expiry = Describe(lifespan);
        var text =
            "Confirm your email address by opening this link:\n\n" +
            $"{link}\n\n" +
            $"The link expires in {expiry}. If you did not create an account, ignore this email.\n";
        var encodedLink = HtmlEncoder.Default.Encode(link);
        var html =
            "<p>Confirm your email address by opening this link:</p>" +
            $"<p><a href=\"{encodedLink}\">Confirm email address</a></p>" +
            $"<p>The link expires in {HtmlEncoder.Default.Encode(expiry)}. If you did not create an account, ignore this email.</p>";

        return new EmailMessage(to, EmailVerificationSubject, text, html);
    }

    /// <summary>The email letting <paramref name="to"/> choose a new password by opening <paramref name="link"/>.</summary>
    public static EmailMessage PasswordReset(string to, string link, TimeSpan lifespan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(link);

        var expiry = Describe(lifespan);
        var text =
            "Choose a new password by opening this link:\n\n" +
            $"{link}\n\n" +
            $"The link expires in {expiry} and works once. If you did not ask to reset your password, ignore this email; your password has not changed.\n";
        var encodedLink = HtmlEncoder.Default.Encode(link);
        var html =
            "<p>Choose a new password by opening this link:</p>" +
            $"<p><a href=\"{encodedLink}\">Reset password</a></p>" +
            $"<p>The link expires in {HtmlEncoder.Default.Encode(expiry)} and works once. If you did not ask to reset your password, ignore this email; your password has not changed.</p>";

        return new EmailMessage(to, PasswordResetSubject, text, html);
    }

    /// <summary>
    /// <paramref name="pageUrl"/> with the <c>userId</c> and <c>token</c> query parameters appended (URL-encoded).
    /// Existing query parameters of the page are kept.
    /// </summary>
    public static string BuildLink(string pageUrl, Guid userId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);
        ArgumentException.ThrowIfNullOrEmpty(token);

        return QueryHelpers.AddQueryString(pageUrl, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [UserIdParameter] = userId.ToString(),
            [TokenParameter] = token,
        });
    }

    /// <summary>A human-readable lifespan, for example <c>1 hour</c>, <c>3 hours</c> or <c>30 minutes</c>.</summary>
    internal static string Describe(TimeSpan lifespan)
    {
        if (lifespan >= TimeSpan.FromHours(1) && lifespan.Ticks % TimeSpan.TicksPerHour == 0)
        {
            var hours = (long)lifespan.TotalHours;
            return hours == 1 ? "1 hour" : string.Create(CultureInfo.InvariantCulture, $"{hours} hours");
        }

        var minutes = Math.Max(1, (long)Math.Ceiling(lifespan.TotalMinutes));
        return minutes == 1 ? "1 minute" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes");
    }
}
