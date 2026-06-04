using Themia.Mediator.Configuration;

namespace Themia.Mediator.Abstractions;

/// <summary>
/// Factory for creating cache keys from requests.
/// </summary>
public interface ICacheKeyFactory
{
    /// <summary>
    /// Creates a cache key for the given request.
    /// If the request implements <see cref="ICacheKeyProvider"/>, uses the custom key.
    /// Otherwise, generates a default key based on request type and properties.
    /// </summary>
    /// <typeparam name="TRequest">The type of request.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <returns>A unique cache key for the request.</returns>
    string CreateKey<TRequest>(TRequest request);

    /// <summary>
    /// Creates a type-based prefix for cache keys of a given request type.
    /// Used for indexing and type-based invalidation.
    /// </summary>
    /// <param name="requestType">The request type.</param>
    /// <returns>A prefix string (e.g., "QueryType:MyApp.Queries.GetOrderQuery").</returns>
    string CreateTypePrefix(Type requestType);

    /// <summary>
    /// Creates a scope root identifier for automatic invalidation based on naming conventions.
    /// Extracts the entity/resource name from the request type name.
    /// </summary>
    /// <param name="requestType">The request type.</param>
    /// <param name="options">Caching options containing known suffixes and prefixes.</param>
    /// <returns>
    /// A scope root identifier (e.g., "Scope:Order" from "GetOrderQuery" or "UpdateOrderCommand").
    /// Returns null if no recognizable pattern is found.
    /// </returns>
    string? CreateScopeRoot(Type requestType, MediatorCachingOptions options);
}
