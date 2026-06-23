using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Notifications.Entities;

/// <summary>A persisted, queryable in-app notification (written directly, not via the outbox).</summary>
public sealed class InAppNotification : AuditableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The recipient user identifier.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Short title/heading.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Rendered body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Whether the recipient has read it.</summary>
    public bool IsRead { get; set; }

    /// <summary>When it was read, if read.</summary>
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>Assigns the identifier for a new (transient) notification.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
