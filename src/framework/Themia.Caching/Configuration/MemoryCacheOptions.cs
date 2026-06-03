namespace Themia.Caching;

/// <summary>
/// Configuration options for memory cache.
/// </summary>
public sealed class MemoryCacheOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether memory caching is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum size of the cache.
    /// </summary>
    public long? SizeLimit { get; set; }

    /// <summary>
    /// Gets or sets the amount to compact the cache by when the maximum size is exceeded.
    /// </summary>
    public double CompactionPercentage { get; set; } = 0.25;

    /// <summary>
    /// Gets or sets the minimum time between scans for expired items.
    /// </summary>
    public TimeSpan? ExpirationScanFrequency { get; set; }
}
