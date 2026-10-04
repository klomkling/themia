namespace Themia.Storage.Cloudflare;

/// <summary>Configuration for the Cloudflare cache purge (coord #0153). Off unless <see cref="Enabled"/> is set.</summary>
public sealed class CloudflarePurgeOptions
{
    /// <summary>
    /// Whether public deletes are purged from the edge. <see langword="false"/> registers a no-op purger, so the
    /// decorator can stay wired in an environment with no CDN (for example Local development).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The Cloudflare zone id that serves the public base URL's host. Required when <see cref="Enabled"/>.</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>
    /// A Cloudflare API token scoped to Zone, Cache Purge on that one zone. It is a separate credential from the
    /// storage (R2) keys and must not be the same token. Sent only as a Bearer header; never logged. Required when <see cref="Enabled"/>.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;
}
