using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace Themia.Geo.Google;

/// <summary>
/// <see cref="IGeocodingProvider"/> over the Google Geocoding API, ported from
/// <c>ProjectGeocodingService.TryGeocodeAsync</c> (ezy-assets).
/// </summary>
/// <remarks>
/// Maps Google's <c>status</c> field: <c>OK</c> → <see cref="GeocodeOutcome.Found"/>,
/// <c>ZERO_RESULTS</c> → <see cref="GeocodeOutcome.NotFound"/>, <c>OVER_QUERY_LIMIT</c> and
/// <c>OVER_DAILY_LIMIT</c> → <see cref="GeocodeOutcome.ProviderLimit"/>, everything else — including any
/// non-success HTTP status, a malformed <c>OK</c> payload, and any transport failure (DNS, connection
/// refused, TLS, a reset, or <see cref="HttpClient"/>'s own timeout) — → <see cref="GeocodeOutcome.ProviderError"/>.
/// </remarks>
public sealed class GoogleGeocodingProvider(
    IHttpClientFactory httpClientFactory, IOptions<GoogleGeocodingOptions> googleOptions) : IGeocodingProvider
{
    /// <summary>The named <see cref="HttpClient"/> this provider resolves via <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "Themia.Geo.Google";

    private static readonly Uri GeocodeEndpoint = new("https://maps.googleapis.com/maps/api/geocode/json");


    /// <inheritdoc />
    public async Task<GeocodeResult> GeocodeAsync(
        string query, GeocodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        try
        {
            return await SendAsync(query, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's own token is still clear, so this is HttpClient's own Timeout firing rather
            // than the caller giving up. Caller cancellation leaves the token set, does not match this
            // filter, and propagates untouched.
            return new GeocodeResult(GeocodeOutcome.ProviderError, null, "transport timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Nothing sits above this provider to turn an exception into an outcome, so throwing here
            // aborts an adopter's whole backfill loop over one unreachable call. ProviderError is what
            // the contract already says a provider-side failure looks like: one row fails, the loop goes
            // on. The exception's type is reported and its message is not — a message can name the
            // request's host, and this provider's API key travels in the request URI.
            return new GeocodeResult(GeocodeOutcome.ProviderError, null, $"transport failure: {ex.GetType().Name}");
        }
    }

    private async Task<GeocodeResult> SendAsync(
        string query, GeocodeOptions? options, CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var requestUri = BuildRequestUri(query, options);

        using var response = await httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new GeocodeResult(GeocodeOutcome.ProviderError, null, $"HTTP {(int)response.StatusCode}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapResponse(document.RootElement);
        }
    }

    private static GeocodeResult MapResponse(JsonElement root)
    {
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString() ?? string.Empty
            : string.Empty;

        return status switch
        {
            "OK" => ExtractPoint(root, status),
            "ZERO_RESULTS" => new GeocodeResult(GeocodeOutcome.NotFound, null, status),
            "OVER_QUERY_LIMIT" or "OVER_DAILY_LIMIT" => new GeocodeResult(GeocodeOutcome.ProviderLimit, null, status),
            _ => new GeocodeResult(GeocodeOutcome.ProviderError, null, status),
        };
    }

    private static GeocodeResult ExtractPoint(JsonElement root, string status)
    {
        if (root.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array &&
            results.GetArrayLength() > 0 &&
            results[0].TryGetProperty("geometry", out var geometry) &&
            geometry.TryGetProperty("location", out var location) &&
            location.TryGetProperty("lat", out var latElement) &&
            location.TryGetProperty("lng", out var lngElement) &&
            latElement.ValueKind == JsonValueKind.Number &&
            lngElement.ValueKind == JsonValueKind.Number &&
            GeoPoint.TryCreate(latElement.GetDouble(), lngElement.GetDouble(), out var point))
        {
            return new GeocodeResult(GeocodeOutcome.Found, point, status);
        }

        // "OK" with a shape the parser cannot read is still a provider-side surprise, not a match —
        // there is no "malformed" outcome in the contract, so this falls to ProviderError like any
        // other unexpected response.
        return new GeocodeResult(GeocodeOutcome.ProviderError, null, status);
    }

    private Uri BuildRequestUri(string query, GeocodeOptions? options)
    {
        var builder = new StringBuilder(GeocodeEndpoint.ToString());
        builder.Append("?address=").Append(Uri.EscapeDataString(query));
        builder.Append("&key=").Append(Uri.EscapeDataString(googleOptions.Value.ApiKey));

        if (!string.IsNullOrWhiteSpace(options?.Region))
        {
            builder.Append("&region=").Append(Uri.EscapeDataString(options.Region));
        }

        if (!string.IsNullOrWhiteSpace(options?.Language))
        {
            builder.Append("&language=").Append(Uri.EscapeDataString(options.Language));
        }

        return new Uri(builder.ToString());
    }
}
