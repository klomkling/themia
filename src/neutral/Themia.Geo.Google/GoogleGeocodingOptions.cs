namespace Themia.Geo.Google;

/// <summary>Configuration for <see cref="GoogleGeocodingProvider"/>.</summary>
public sealed class GoogleGeocodingOptions
{
    /// <summary>
    /// The Google Geocoding API key. Sent as the <c>key</c> query parameter on every request — Google
    /// has no header form for it. <c>AddThemiaGeoGoogle</c> (in
    /// <c>Themia.Geo.Google.DependencyInjection</c>) suppresses <c>IHttpClientFactory</c>'s default
    /// request-URI logging so this value never reaches a log; do not add logging that reintroduces it.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
