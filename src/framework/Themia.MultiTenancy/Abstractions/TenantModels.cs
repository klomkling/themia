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
/// <remarks>
/// Constructor invariants (enforced regardless of call site):
/// <list type="bullet">
///   <item>When <see cref="Success"/> is <c>true</c>, at least one of <see cref="Tenant"/> or
///   <see cref="Identifier"/> must be non-null/non-whitespace.</item>
///   <item>When <see cref="Success"/> is <c>false</c>, <see cref="Tenant"/> must be <c>null</c>.</item>
/// </list>
/// Use the static factory methods (<see cref="NotFound"/>, <see cref="Resolved"/>,
/// <see cref="Identified"/>) as the preferred creation API.
/// </remarks>
public sealed record TenantResolutionResult
{
    /// <summary>Gets whether the resolution succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the resolved tenant, or <c>null</c> when only an identifier was resolved.</summary>
    public TenantInfo? Tenant { get; init; }

    /// <summary>Gets the resolved tenant identifier.</summary>
    public string? Identifier { get; init; }

    /// <summary>Gets the strategy source name that produced this result.</summary>
    public string? Source { get; init; }

    /// <summary>Gets an optional human-readable reason for a failed resolution.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Initializes a new <see cref="TenantResolutionResult"/> and enforces type invariants.
    /// </summary>
    /// <param name="Success">Whether the resolution succeeded.</param>
    /// <param name="Tenant">The resolved tenant (may be <c>null</c> when <paramref name="Identifier"/> is provided).</param>
    /// <param name="Identifier">The resolved identifier (optional).</param>
    /// <param name="Source">The strategy source that produced the result (optional).</param>
    /// <param name="Reason">Human-readable reason for a failed resolution (optional).</param>
    public TenantResolutionResult(bool Success, TenantInfo? Tenant, string? Identifier = null, string? Source = null, string? Reason = null)
    {
        if (Success && Tenant is null && string.IsNullOrWhiteSpace(Identifier))
        {
            throw new ArgumentException(
                "A successful TenantResolutionResult must carry a non-null Tenant or a non-whitespace Identifier.",
                nameof(Tenant));
        }

        if (!Success && Tenant is not null)
        {
            throw new ArgumentException(
                "A failed TenantResolutionResult must not carry a non-null Tenant.",
                nameof(Tenant));
        }

        this.Success = Success;
        this.Tenant = Tenant;
        this.Identifier = Identifier;
        this.Source = Source;
        this.Reason = Reason;
    }

    /// <summary>
    /// Creates a failed resolution result for the given source.
    /// </summary>
    public static TenantResolutionResult NotFound(string source, string? reason = null) =>
        new(false, null, null, source, reason);

    /// <summary>
    /// Creates a successful resolution result carrying the fully resolved tenant.
    /// </summary>
    public static TenantResolutionResult Resolved(TenantInfo tenant, string source) =>
        new(true, tenant, tenant.Identifier, source, null);

    /// <summary>
    /// Creates a successful resolution result carrying only the resolved identifier.
    /// </summary>
    public static TenantResolutionResult Identified(string identifier, string source) =>
        new(true, null, identifier, source, null);
}

/// <summary>
/// Accessor for the current tenant within a request scope.
/// </summary>
/// <remarks>
/// This interface is intentionally read-only. Only infrastructure that owns the request
/// lifecycle (i.e. <c>TenantResolutionMiddleware</c>) should write the ambient tenant, and
/// it does so via <see cref="ITenantSetter"/>. Application code and business logic should
/// depend only on <see cref="ITenantAccessor"/> to prevent accidental mutation of the
/// ambient tenant mid-request.
/// </remarks>
public interface ITenantAccessor
{
    /// <summary>
    /// Gets the current tenant for the active request scope.
    /// </summary>
    TenantInfo? Current { get; }
}

/// <summary>
/// Allows the tenant resolution infrastructure to write the ambient tenant for the current
/// request scope. Only <c>TenantResolutionMiddleware</c> (or equivalent request-lifecycle
/// infrastructure) should depend on this interface; application code must depend only on
/// <see cref="ITenantAccessor"/>.
/// </summary>
public interface ITenantSetter
{
    /// <summary>
    /// Sets the current tenant for the active request scope.
    /// </summary>
    /// <param name="tenant">The resolved tenant, or <c>null</c> to clear the ambient tenant.</param>
    void Set(TenantInfo? tenant);
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
