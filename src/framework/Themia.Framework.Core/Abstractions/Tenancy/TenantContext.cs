namespace Themia.Framework.Core.Abstractions.Tenancy;

/// <summary>
/// Provides ambient tenant information.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Gets the current tenant identifier, if resolved.
    /// </summary>
    TenantId? CurrentTenantId { get; }

    /// <summary>
    /// Indicates whether a tenant is available.
    /// </summary>
    bool HasTenant => CurrentTenantId is not null;

    /// <summary>
    /// Gets the source used to resolve the tenant (e.g., header or route).
    /// </summary>
    string? Source { get; }
}

/// <summary>
/// Default implementation of <see cref="ITenantContext"/>.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    /// <summary>
    /// Initializes a new tenant context.
    /// </summary>
    /// <param name="tenantId">Current tenant id.</param>
    /// <param name="source">Source used to determine the tenant.</param>
    public TenantContext(TenantId? tenantId, string? source = null)
    {
        CurrentTenantId = tenantId;
        Source = source;
    }

    /// <inheritdoc />
    public TenantId? CurrentTenantId { get; }

    /// <inheritdoc />
    public string? Source { get; }

    /// <inheritdoc />
    public bool HasTenant => CurrentTenantId is not null;
}

/// <summary>
/// Marks an entity as belonging to a specific tenant.
/// </summary>
/// <remarks>
/// Implementations should use either 'get; set;' or 'get; init;' depending on mutability requirements.
/// EF Core requires a setter for materialization. Use 'private set' or 'init' for immutable entities.
/// </remarks>
public interface ITenantEntity
{
    /// <summary>
    /// Gets or sets the tenant identifier associated with the entity.
    /// </summary>
    TenantId? TenantId { get; set; }
}
