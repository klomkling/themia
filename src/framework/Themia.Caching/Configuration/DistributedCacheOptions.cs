namespace Themia.Caching;

/// <summary>
/// Configuration options for distributed cache (Redis/Garnet/Valkey).
/// </summary>
public sealed class DistributedCacheOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether distributed caching is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the connection string for the distributed cache.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the instance name prefix for cache keys.
    /// </summary>
    public string InstanceName { get; set; } = "Themia:";

    /// <summary>
    /// Gets or sets the cache provider type (Redis, Garnet, or Valkey).
    /// </summary>
    public string Provider { get; set; } = "Redis";
}
