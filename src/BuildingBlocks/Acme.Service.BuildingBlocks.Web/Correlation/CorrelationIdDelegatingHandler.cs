namespace Acme.Service.BuildingBlocks.Web.Correlation;

/// <summary>Forwards the current correlation id on outbound <see cref="HttpClient"/> calls (D-25).</summary>
public sealed class CorrelationIdDelegatingHandler(ICorrelationIdAccessor accessor) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var correlationId = accessor.CorrelationId;
        if (correlationId is not null && !request.Headers.Contains(CorrelationId.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
