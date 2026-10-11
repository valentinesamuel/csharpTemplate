using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Acme.Service.BuildingBlocks.Infra.Smtp;

/// <summary>
/// Settings of the SMTP notification provider (section <c>Notifications:Smtp</c>). Validated at startup (INV-17);
/// validation messages name the full key but never echo the password (INV-28).
/// </summary>
/// <remarks>
/// <see cref="Username"/> and <see cref="Password"/> are set together or not at all, and only with
/// <see cref="UseStartTls"/> enabled, so credentials never cross the network in plain text. Treat the password as a
/// secret: supply it from the environment or a secret store, never from appsettings.json (INV-11).
/// </remarks>
public sealed class SmtpOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Notifications:Smtp";

    /// <summary>Full key of <see cref="Host"/>.</summary>
    public const string HostKey = SectionName + ":Host";

    /// <summary>Full key of <see cref="Port"/>.</summary>
    public const string PortKey = SectionName + ":Port";

    /// <summary>Full key of <see cref="UseStartTls"/>.</summary>
    public const string UseStartTlsKey = SectionName + ":UseStartTls";

    /// <summary>Full key of <see cref="Username"/>.</summary>
    public const string UsernameKey = SectionName + ":Username";

    /// <summary>Full key of <see cref="Password"/>.</summary>
    public const string PasswordKey = SectionName + ":Password";

    /// <summary>Full key of <see cref="From"/>.</summary>
    public const string FromKey = SectionName + ":From";

    /// <summary>Default submission port (STARTTLS).</summary>
    public const int DefaultPort = 587;

    /// <summary>Default connect, authenticate and send timeout (plan P10: 30 s).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>SMTP server host name or IP address. Required.</summary>
    public string? Host { get; set; }

    /// <summary>SMTP server port. Default 587.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Upgrade the connection with STARTTLS and fail when the server does not offer it. Default true. Set false only
    /// for a local development catcher such as Mailpit; credentials are then rejected by validation.
    /// </summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>SMTP user name; set together with <see cref="Password"/>, or leave both empty for no authentication.</summary>
    public string? Username { get; set; }

    /// <summary>SMTP password. A secret; never logged or echoed.</summary>
    public string? Password { get; set; }

    /// <summary>Sender mailbox, for example <c>Service &lt;no-reply@example.com&gt;</c>. Required.</summary>
    public string? From { get; set; }

    /// <summary>
    /// Timeout of each network step (connect, authenticate, send). Code-level setting, not bound from configuration
    /// (§0.7 defines no key). Default 30 s.
    /// </summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>Set by <see cref="Read"/> when <see cref="PortKey"/> is not an integer.</summary>
    internal bool PortInvalid { get; set; }

    /// <summary>Set by <see cref="Read"/> when <see cref="UseStartTlsKey"/> is not a boolean.</summary>
    internal bool UseStartTlsInvalid { get; set; }

    /// <summary>True when credentials are configured.</summary>
    internal bool HasCredentials => Username is not null && Password is not null;

    /// <summary>The parsed <see cref="From"/> mailbox, or null when it is unset or invalid.</summary>
    internal MailboxAddress? FromAddress =>
        From is not null && MailboxAddress.TryParse(ParserOptions.Default, From, out var address) && IsAddress(address)
            ? address
            : null;

    /// <summary>Reads the section from <paramref name="configuration"/>; unparsable values are flagged for <see cref="Validator"/>.</summary>
    internal static void Read(IConfiguration configuration, SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        options.Host = Normalize(configuration[HostKey]);
        options.Username = Normalize(configuration[UsernameKey]);

        // Passwords are taken verbatim (leading/trailing spaces may be significant); only empty means "not set".
        var password = configuration[PasswordKey];
        options.Password = string.IsNullOrEmpty(password) ? null : password;
        options.From = Normalize(configuration[FromKey]);

        var port = Normalize(configuration[PortKey]);
        if (port is not null)
        {
            options.PortInvalid = !int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort);
            options.Port = options.PortInvalid ? options.Port : parsedPort;
        }

        var useStartTls = Normalize(configuration[UseStartTlsKey]);
        if (useStartTls is not null)
        {
            options.UseStartTlsInvalid = !bool.TryParse(useStartTls, out var parsedStartTls);
            options.UseStartTls = options.UseStartTlsInvalid ? options.UseStartTls : parsedStartTls;
        }
    }

    /// <summary>True when <paramref name="address"/> has a local part and a domain (<c>user@domain</c>).</summary>
    internal static bool IsAddress(MailboxAddress address)
    {
        var at = address.Address.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < address.Address.Length - 1;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Startup validation of <see cref="SmtpOptions"/>. Never includes the password (or any value) in a message.</summary>
    internal sealed class Validator : IValidateOptions<SmtpOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, SmtpOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var failures = new List<string>();

            if (options.Host is null)
            {
                failures.Add($"{HostKey} is required when Notifications:Provider is {SmtpRegistration.ProviderName}.");
            }
            else if (Uri.CheckHostName(options.Host) == UriHostNameType.Unknown)
            {
                failures.Add($"{HostKey} must be a host name or an IP address.");
            }

            if (options.PortInvalid || options.Port is < 1 or > 65535)
            {
                failures.Add($"{PortKey} must be an integer between 1 and 65535.");
            }

            if (options.UseStartTlsInvalid)
            {
                failures.Add($"{UseStartTlsKey} must be 'true' or 'false'.");
            }

            if ((options.Username is null) != (options.Password is null))
            {
                failures.Add($"{UsernameKey} and {PasswordKey} must be set together, or both left empty for no authentication.");
            }
            else if (options.HasCredentials && !options.UseStartTls)
            {
                failures.Add($"{UsernameKey} and {PasswordKey} require {UseStartTlsKey}=true; credentials are never sent over an unencrypted connection.");
            }

            if (options.From is null)
            {
                failures.Add($"{FromKey} is required when Notifications:Provider is {SmtpRegistration.ProviderName}.");
            }
            else if (options.FromAddress is null)
            {
                failures.Add($"{FromKey} must be a mailbox such as 'no-reply@example.com' or 'Service <no-reply@example.com>'.");
            }

            if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5))
            {
                failures.Add($"{SectionName} timeout must be greater than 0 and at most 00:05:00.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
