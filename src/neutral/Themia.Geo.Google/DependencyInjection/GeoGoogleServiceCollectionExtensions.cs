using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Themia.Geo.Google.DependencyInjection;

/// <summary>DI entry point for the Google Geocoding provider.</summary>
public static class GeoGoogleServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GoogleGeocodingOptions"/> (validated with <c>ValidateOnStart</c>) and
    /// <see cref="GoogleGeocodingProvider"/> as <see cref="IGeocodingProvider"/>, plus its named
    /// <see cref="HttpClient"/> (<see cref="GoogleGeocodingProvider.HttpClientName"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets <see cref="GoogleGeocodingOptions.ApiKey"/> and any other options.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddThemiaGeoGoogle(
        this IServiceCollection services, Action<GoogleGeocodingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<GoogleGeocodingOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "GoogleGeocodingOptions.ApiKey must be set.")
            .ValidateOnStart();

        // Google's Geocoding API takes the key only as the `key` query parameter — there is no header
        // form (verified against the live endpoint: sending the key as `X-Goog-Api-Key` gets the same
        // "You must use an API key" rejection as sending none). So the key is necessarily in every
        // request URI.
        //
        // Microsoft.Extensions.Http's LoggingHttpMessageHandler already redacts the query string of a
        // logged request URI by default (System.Net.Http.UriRedactionHelper), so today this client's
        // logs read "...geocode/json?*" rather than the key. But that redaction is a process-wide
        // AppContext switch (DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION /
        // "System.Net.Http.DisableUriRedaction"), not something this package controls — an operator
        // debugging an unrelated HTTP issue could flip it on to see full URIs and start writing this key
        // into the host's logs without any reason to connect the two. RemoveAllLoggers() drops the
        // framework's request-URI logging for THIS named client only (nothing else on the host is
        // affected), making the protection ours regardless of that switch. See
        // GoogleGeocodingProviderTests for the pair of tests that prove this: one with the switch off
        // (the default) and one with it deliberately flipped on.
        services.AddHttpClient(GoogleGeocodingProvider.HttpClientName)
            .RemoveAllLoggers();

        services.TryAddSingleton<IGeocodingProvider, GoogleGeocodingProvider>();
        return services;
    }
}
