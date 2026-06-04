namespace Themia.Mediator.Attributes;

/// <summary>
/// Marks a request as cacheable with optional expiration configuration.
/// When both this attribute and <see cref="Themia.Mediator.Abstractions.ICacheable{TResponse}"/> are present,
/// attribute values take precedence over interface values.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CacheableAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the absolute expiration time in seconds.
    /// Use a value > 0 to enable, or -1 to indicate not set (fallback to interface/global default).
    /// </summary>
    public int AbsoluteExpirationSeconds { get; set; } = -1;

    /// <summary>
    /// Gets or sets the sliding expiration time in seconds.
    /// The cache entry will be removed if not accessed within this timespan.
    /// Use a value > 0 to enable, or -1 to indicate not set (fallback to interface/global default).
    /// </summary>
    public int SlidingExpirationSeconds { get; set; } = -1;
}
