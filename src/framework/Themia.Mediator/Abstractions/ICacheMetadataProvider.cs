using Themia.Mediator.Infrastructure;

namespace Themia.Mediator.Abstractions;

/// <summary>
/// Provides unified cache metadata for requests by combining information from
/// attributes and interfaces.
/// </summary>
public interface ICacheMetadataProvider
{
    /// <summary>
    /// Gets the cache metadata for a request type and optional instance.
    /// </summary>
    /// <param name="requestType">The type of the request.</param>
    /// <param name="requestInstance">
    /// Optional request instance. When provided, interface-based values can be read.
    /// </param>
    /// <returns>
    /// Cache metadata combining attribute and interface information.
    /// Attribute values take precedence over interface values for expirations.
    /// Invalidation types from both sources are unioned.
    /// </returns>
    CacheMetadata Get(Type requestType, object? requestInstance);
}
