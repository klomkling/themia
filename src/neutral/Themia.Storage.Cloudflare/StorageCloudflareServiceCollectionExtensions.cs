using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.Storage.Cloudflare;

/// <summary>Registers the Cloudflare cache purge (coord #0153).</summary>
public static class StorageCloudflareServiceCollectionExtensions
{
    // Chosen, not a Cloudflare figure: a purge sits inside a delete, and HttpClient's 100 s default would stall a queue worker.
    private static readonly TimeSpan PurgeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Registers <see cref="ICdnPurger"/> backed by Cloudflare, off unless <see cref="CloudflarePurgeOptions.Enabled"/>
    /// is set. With it off the registered purger does nothing, so <see cref="PurgingStorageProvider"/> can stay wired
    /// in every environment. With it on, a blank <see cref="CloudflarePurgeOptions.ZoneId"/> or
    /// <see cref="CloudflarePurgeOptions.ApiToken"/> fails host start.
    /// Wiring the decorator is the app's job; see the package README.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options; bind them from configuration in the app.</param>
    public static IServiceCollection AddThemiaStorageCloudflarePurge(
        this IServiceCollection services, Action<CloudflarePurgeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<CloudflarePurgeOptions>()
            .Configure(configure)
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ZoneId), "CloudflarePurgeOptions.ZoneId must be set when Enabled is true.")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ApiToken), "CloudflarePurgeOptions.ApiToken must be set when Enabled is true.")
            .Validate(
                o => !o.Enabled || (o.ApiToken ?? string.Empty).All(c => !char.IsWhiteSpace(c) && !char.IsControl(c)),
                "CloudflarePurgeOptions.ApiToken must not contain whitespace or control characters (trim a trailing newline read from a secret file).")
            .ValidateOnStart();

        // The token goes in a per-request Authorization header, never in a URL. Redact that header in the
        // framework's request logging explicitly rather than relying on a default.
        services.AddHttpClient(CloudflareCdnPurger.HttpClientName, client =>
            {
                client.BaseAddress = CloudflareCdnPurger.ApiBaseAddress;
                client.Timeout = PurgeTimeout;
            })
            .RedactLoggedHeaders(["Authorization"]);

        services.TryAddSingleton<ICdnPurger>(sp =>
            sp.GetRequiredService<IOptions<CloudflarePurgeOptions>>().Value.Enabled
                ? new CloudflareCdnPurger(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<CloudflarePurgeOptions>>())
                : NoOpCdnPurger.Instance);
        return services;
    }
}
