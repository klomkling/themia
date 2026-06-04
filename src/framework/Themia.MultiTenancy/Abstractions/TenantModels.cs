using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Themia.MultiTenancy.Abstractions;

/// <summary>
/// Represents a tenant and its metadata.
/// </summary>
/// <remarks>
/// <see cref="ConnectionString"/> is credential-bearing: it is excluded from JSON serialization
/// (<see cref="JsonIgnoreAttribute"/>) and redacted in <see cref="ToString"/> so it cannot leak into
/// logs or API responses. The value is still readable in code via the property for the DB-per-tenant
/// connection seam consumed by the data layer.
/// </remarks>
public sealed record TenantInfo(
    string Id,
    string Identifier,
    string? Name = null,
    string? Environment = null,
    [property: JsonIgnore] string? ConnectionString = null,
    IReadOnlyDictionary<string, string>? Properties = null)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyProperties =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>
    /// Gets the custom properties/metadata for this tenant.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        Properties is null
            ? EmptyProperties
            : Properties is ReadOnlyDictionary<string, string>
                ? Properties
                : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Properties));

    /// <summary>
    /// Returns a diagnostic string with the credential-bearing connection string redacted.
    /// </summary>
    public override string ToString() =>
        $"TenantInfo {{ Id = {Id}, Identifier = {Identifier}, Name = {Name}, " +
        $"Environment = {Environment}, Properties = [{Properties.Count}], " +
        $"ConnectionString = {(ConnectionString is null ? "null" : "***")} }}";
}

/// <summary>
/// Context information for tenant resolution (host, path, headers, claims, etc.).
/// </summary>
public sealed record TenantResolutionContext(
    string? Host,
    string? Path,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Claims)
{
    /// <summary>
    /// Gets an empty resolution context with no host, path, headers, or claims.
    /// </summary>
    public static TenantResolutionContext Empty { get; } = new(
        null,
        null,
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()),
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));
}

/// <summary>
/// Result of a tenant resolution attempt.
/// </summary>
public sealed record TenantResolutionResult(bool Success, TenantInfo? Tenant, string? Identifier = null, string? Source = null, string? Reason = null)
{
    /// <summary>
    /// Creates a failed resolution result for the given source.
    /// </summary>
    public static TenantResolutionResult NotFound(string source, string? reason = null) => new(false, null, null, source, reason);

    /// <summary>
    /// Creates a successful resolution result carrying the fully resolved tenant.
    /// </summary>
    public static TenantResolutionResult Resolved(TenantInfo tenant, string source) => new(true, tenant, tenant.Identifier, source, null);

    /// <summary>
    /// Creates a successful resolution result carrying only the resolved identifier.
    /// </summary>
    public static TenantResolutionResult Identified(string identifier, string source) => new(true, null, identifier, source, null);
}

/// <summary>
/// Accessor for the current tenant within a request scope.
/// </summary>
public interface ITenantAccessor
{
    /// <summary>
    /// Gets or sets the current tenant for the active request scope.
    /// </summary>
    TenantInfo? Current { get; set; }
}

/// <summary>
/// Defines a tenant store (e.g., in-memory, database, configuration).
/// </summary>
public interface ITenantStore
{
    /// <summary>
    /// Finds a tenant by its identifier, or returns null when no tenant matches.
    /// </summary>
    Task<TenantInfo?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional interface for tenant stores that support cache eviction.
/// Implemented by caching decorators to allow manual cache invalidation.
/// </summary>
public interface ICacheableTenantStore : ITenantStore
{
    /// <summary>
    /// Evicts a specific tenant from the cache by identifier.
    /// </summary>
    /// <param name="identifier">The tenant identifier to evict.</param>
    void EvictFromCache(string identifier);

    /// <summary>
    /// Clears all cached tenants.
    /// </summary>
    void ClearCache();
}

/// <summary>
/// Strategy that attempts to resolve a tenant from a context (host, path, headers, claims).
/// </summary>
public interface ITenantResolutionStrategy
{
    /// <summary>
    /// Attempts to resolve a tenant from the given resolution context.
    /// </summary>
    Task<TenantResolutionResult> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregates multiple strategies to resolve a tenant.
/// </summary>
public interface ITenantResolver
{
    /// <summary>
    /// Resolves the tenant for the given context by aggregating the configured strategies.
    /// </summary>
    Task<TenantInfo?> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default);
}
