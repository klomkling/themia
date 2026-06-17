using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Storage.Entities;

/// <summary>Metadata for a stored object. Tenant-scoped when <see cref="ITenantEntity.TenantId"/> is set;
/// a platform object when it is <see langword="null"/>. The blob itself lives in the configured
/// <see cref="Themia.Storage.IStorageProvider"/>; this row is the source of truth for existence and quota.</summary>
public sealed class StorageObject : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The logical key (unprefixed), unique within the tenant.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The stored MIME content type.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The object size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>The backend entity tag, when available.</summary>
    public string? ETag { get; set; }

    /// <summary>When the object's upload was confirmed; null while a presigned reservation is pending (invisible to reads).</summary>
    public DateTimeOffset? CommittedAt { get; set; }

    /// <summary>Assigns the identifier for a new (transient) object.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
