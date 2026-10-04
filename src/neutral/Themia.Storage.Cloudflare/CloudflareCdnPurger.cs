using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Themia.Storage.Cloudflare;

/// <summary>
/// Purges one URL from Cloudflare's cache: <c>POST /zones/{zone}/purge_cache</c> with <c>{"files":[url]}</c>.
/// Anything short of a 2xx answer with <c>success:true</c> throws <see cref="CdnPurgeException"/>; a 200 means
/// Cloudflare accepted the request, not that removal was confirmed.
/// </summary>
public sealed class CloudflareCdnPurger(IHttpClientFactory httpClientFactory, IOptions<CloudflarePurgeOptions> options) : ICdnPurger
{
    /// <summary>The named <see cref="HttpClient"/> this purger resolves via <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "Themia.Storage.Cloudflare";

    internal static readonly Uri ApiBaseAddress = new("https://api.cloudflare.com/client/v4/");

    private const int MaxErrorMessageLength = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc />
    public async Task PurgeAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var settings = options.Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"zones/{Uri.EscapeDataString(settings.ZoneId)}/purge_cache")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new PurgeRequest([url.AbsoluteUri]), JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);

        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var failure = DescribeFailure(response.StatusCode, body);
            if (failure is not null)
            {
                throw Refused(url, failure, (int)response.StatusCode, innerException: null);
            }
        }
        catch (HttpRequestException ex)
        {
            throw Refused(url, "Cloudflare could not be reached.", httpStatus: null, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's token is still clear, so this is HttpClient's own timeout firing.
            throw Refused(url, "The Cloudflare purge timed out.", httpStatus: null, ex);
        }
    }

    private static CdnPurgeException Refused(Uri url, string failure, int? httpStatus, Exception? innerException) =>
        new(
            url,
            $"{failure} The object is already deleted from storage; the CDN may still serve {url.AbsoluteUri}. Repeat the delete and purge.",
            httpStatus,
            innerException);

    private static string? DescribeFailure(HttpStatusCode status, string body)
    {
        var envelope = ParseEnvelope(body);
        var code = (int)status;

        if (code is >= 200 and < 300)
        {
            if (envelope?.Success == true)
            {
                return null;
            }

            return envelope is null
                ? $"Cloudflare answered HTTP {code} with a body that is not its purge response, so the purge cannot be confirmed."
                : $"Cloudflare answered HTTP {code} without success:true{FirstError(envelope)}.";
        }

        return $"Cloudflare refused the purge with HTTP {code}{FirstError(envelope)}.";
    }

    private static string FirstError(PurgeResponse? envelope)
    {
        var error = envelope?.Errors?.FirstOrDefault();
        if (error is null)
        {
            return string.Empty;
        }

        var message = error.Message ?? string.Empty;
        if (message.Length > MaxErrorMessageLength)
        {
            message = message[..MaxErrorMessageLength];
        }

        return $": code {error.Code}, \"{message}\"";
    }

    private static PurgeResponse? ParseEnvelope(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<PurgeResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PurgeRequest([property: JsonPropertyName("files")] string[] Files);

    private sealed record PurgeResponse(bool? Success, List<PurgeError>? Errors);

    private sealed record PurgeError(int? Code, string? Message);
}
