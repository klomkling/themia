using System.Net;

namespace Themia.Notifications.Providers;

/// <summary>Base for HTTP-based SMS providers. Subclasses build the provider request and interpret the
/// response; this base owns the POST + read. Reuse one <see cref="HttpClient"/> (e.g. via
/// <c>IHttpClientFactory</c>) per the .NET HttpClient guidance.</summary>
public abstract class HttpSmsSenderBase(HttpClient httpClient) : ISmsSender
{
    /// <summary>Builds the provider-specific HTTP request for <paramref name="message"/>.</summary>
    protected abstract HttpRequestMessage BuildRequest(NotificationMessage message);

    /// <summary>Maps the provider response to a <see cref="NotificationResult"/>.</summary>
    protected abstract NotificationResult Interpret(HttpStatusCode status, string responseBody);

    /// <inheritdoc />
    public async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        using var request = BuildRequest(message);
        ArgumentNullException.ThrowIfNull(request);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = Interpret(response.StatusCode, body);
        return result ?? throw new InvalidOperationException($"{GetType().Name}.Interpret returned null.");
    }
}
