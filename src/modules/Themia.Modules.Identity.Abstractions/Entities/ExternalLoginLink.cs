using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>Links an external provider identity (provider + subject) to a Themia <see cref="User"/>.
/// A tenant-scoped entity: it is looked up by (provider, external_id) before any user is known, so the
/// framework tenant filter isolates it and the same external account can map to a different user per
/// tenant. No password, no soft-delete (unlink is a hard delete).</summary>
public sealed class ExternalLoginLink : ITenantEntity
{
    /// <summary>The link identifier (UUIDv7).</summary>
    public Guid Id { get; private set; }

    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The linked user.</summary>
    public Guid UserId { get; set; }

    /// <summary>The registered provider key, lowercased (e.g. "google", "line").</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider subject (stable per provider).</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The time the link was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Assigns the client-generated identifier (UUIDv7).</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
