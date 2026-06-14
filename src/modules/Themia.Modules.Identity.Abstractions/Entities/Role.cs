using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A named role granting a set of claims. Platform-wide when <see cref="ITenantEntity.TenantId"/> is null.</summary>
public sealed class Role : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The role name, as entered.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The upper-invariant normalized name used for lookups and uniqueness.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>An optional human-readable description.</summary>
    public string? Description { get; set; }

    /// <summary>Assigns the identifier for a new (transient) role.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
