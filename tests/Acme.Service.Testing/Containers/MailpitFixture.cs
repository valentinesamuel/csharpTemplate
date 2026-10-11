using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Acme.Service.Testing.Containers;

/// <summary>
/// Shared Mailpit SMTP catcher for integration tests (Docker required; generic container, WH-22). Use as a class or
/// collection fixture. SMTP on <see cref="SmtpPort"/> (no TLS, no authentication), HTTP API on <see cref="ApiPort"/>.
/// Tests share the mailbox, so use unique subjects per test and look messages up with <see cref="FindBySubjectAsync"/>.
/// </summary>
/// <remarks>Image <c>axllent/mailpit</c> (MIT licence), pinned below and recorded in state.md.</remarks>
public sealed class MailpitFixture : IAsyncLifetime
{
    /// <summary>Pinned Mailpit image (recorded in state.md).</summary>
    public const string Image = "axllent/mailpit:v1.31.4";

    /// <summary>Container SMTP port.</summary>
    public const ushort SmtpPort = 1025;

    /// <summary>Container HTTP API and web UI port.</summary>
    public const ushort ApiPort = 8025;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(ApiPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ApiPort).ForPath("/readyz")))
        .Build();

    private HttpClient? _api;

    /// <summary>Host name of the SMTP endpoint.</summary>
    public string SmtpHost => _container.Hostname;

    /// <summary>Mapped host port of the SMTP endpoint.</summary>
    public int SmtpHostPort => _container.GetMappedPublicPort(SmtpPort);

    /// <summary>Base address of the HTTP API (<c>http://host:port/</c>).</summary>
    public Uri ApiBaseAddress => new(string.Create(
        CultureInfo.InvariantCulture,
        $"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}/"));

    private HttpClient Api => _api ?? throw new InvalidOperationException("The fixture is not initialized.");

    /// <summary>
    /// Polls the Mailpit API until a message with exactly <paramref name="subject"/> arrives, then returns it with its
    /// bodies. Throws <see cref="TimeoutException"/> when none arrives within <paramref name="timeout"/>.
    /// </summary>
    public async Task<MailpitMessage> FindBySubjectAsync(string subject, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var query = Uri.EscapeDataString($"subject:\"{subject}\"");
                var found = await Api.GetFromJsonAsync<MailpitSearchResult>(new Uri($"api/v1/search?query={query}", UriKind.Relative), deadline.Token)
                    .ConfigureAwait(false);
                var summary = found?.Messages.FirstOrDefault(m => string.Equals(m.Subject, subject, StringComparison.Ordinal));
                if (summary is not null)
                {
                    return await Api.GetFromJsonAsync<MailpitMessage>(new Uri($"api/v1/message/{summary.Id}", UriKind.Relative), deadline.Token)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Mailpit returned no body for message {summary.Id}.");
                }

                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No message with subject '{subject}' reached Mailpit within {timeout}.");
        }
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        _api = new HttpClient { BaseAddress = ApiBaseAddress };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _api?.Dispose();
        await _container.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Result of Mailpit's <c>GET /api/v1/search</c> (the fields the tests use).</summary>
public sealed class MailpitSearchResult
{
    /// <summary>The matching message summaries, newest first.</summary>
    [JsonPropertyName("messages")]
    public IReadOnlyList<MailpitMessageSummary> Messages { get; init; } = [];
}

/// <summary>A message summary from the Mailpit API.</summary>
public sealed class MailpitMessageSummary
{
    /// <summary>Mailpit message id.</summary>
    [JsonPropertyName("ID")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Subject.</summary>
    [JsonPropertyName("Subject")]
    public string Subject { get; init; } = string.Empty;
}

/// <summary>A message from Mailpit's <c>GET /api/v1/message/{id}</c> (the fields the tests use).</summary>
public sealed class MailpitMessage
{
    /// <summary>Subject.</summary>
    [JsonPropertyName("Subject")]
    public string Subject { get; init; } = string.Empty;

    /// <summary>Sender.</summary>
    [JsonPropertyName("From")]
    public MailpitAddress? From { get; init; }

    /// <summary>Recipients.</summary>
    [JsonPropertyName("To")]
    public IReadOnlyList<MailpitAddress> To { get; init; } = [];

    /// <summary>Plain-text body.</summary>
    [JsonPropertyName("Text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>HTML body (empty when none).</summary>
    [JsonPropertyName("HTML")]
    public string Html { get; init; } = string.Empty;
}

/// <summary>A mailbox in the Mailpit API.</summary>
public sealed class MailpitAddress
{
    /// <summary>Display name.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Address.</summary>
    [JsonPropertyName("Address")]
    public string Address { get; init; } = string.Empty;
}
