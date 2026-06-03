namespace Themia.Caching;

/// <summary>
/// Configuration options for Themia caching.
/// </summary>
public sealed class CachingOptions
{
    /// <summary>
    /// The configuration section name for Themia caching.
    /// </summary>
    public const string SectionName = "Themia:Caching";

    /// <summary>
    /// Gets or sets the memory cache options.
    /// </summary>
    public MemoryCacheOptions MemoryCache { get; set; } = new();

    /// <summary>
    /// Gets or sets the distributed cache options.
    /// </summary>
    public DistributedCacheOptions DistributedCache { get; set; } = new();

    /// <summary>
    /// Gets or sets the serialization options.
    /// </summary>
    public SerializationOptions Serialization { get; set; } = new();
}
