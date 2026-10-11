using System.Net.Http.Json;
using System.Text.Json;

namespace Acme.Service.Testing;

/// <summary>Reads the success envelope (ADR-0017): <c>{ isSuccess, requestId, correlationId, data }</c>.</summary>
public static class ApiEnvelopeExtensions
{
    /// <summary>Parses the envelope and deserializes its <c>data</c> member as <typeparamref name="T"/>.</summary>
    public static async Task<T?> ReadDataAsync<T>(this HttpContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var data = await content.ReadDataElementAsync(cancellationToken);
        return data.Deserialize<T>(JsonSerializerOptions.Web);
    }

    /// <summary>Parses the envelope and returns a clone of its <c>data</c> member (safe to use after the call).</summary>
    public static async Task<JsonElement> ReadDataElementAsync(this HttpContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var document = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("data").Clone();
    }

    /// <summary>Returns a clone of the raw JSON body, for problem responses and endpoints outside the envelope.</summary>
    public static async Task<JsonElement> ReadJsonAsync(this HttpContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var document = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }
}
