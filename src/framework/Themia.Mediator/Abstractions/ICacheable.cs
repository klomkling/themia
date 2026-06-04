namespace Themia.Mediator.Abstractions;

/// <summary>
/// Marker interface for requests whose responses should be cached.
/// Implement this interface to enable caching for a request with configurable expiration.
/// </summary>
/// <typeparam name="TResponse">The type of response returned by the request.</typeparam>
public interface ICacheable<TResponse> : IRequest<TResponse>
{
    /// <summary>
    /// Gets the absolute expiration time for the cached response.
    /// If null, falls back to the global default or no absolute expiration.
    /// </summary>
    TimeSpan? AbsoluteExpiration => null;

    /// <summary>
    /// Gets the sliding expiration time for the cached response.
    /// The cache entry will be removed if not accessed within this timespan.
    /// If null, falls back to the global default or no sliding expiration.
    /// </summary>
    TimeSpan? SlidingExpiration => null;
}
