using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Entities;

/// <summary>
/// Whether a channel is enabled for a tenant (and optionally a specific user),
/// plus the preferred locale. A null <see cref="UserId"/> is the tenant-wide default.
/// </summary>
public sealed class NotificationPreference : AuditableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The user this preference applies to, or null for the tenant-wide default.</summary>
    public string? UserId { get; set; }

    /// <summary>The channel this preference governs.</summary>
    public NotificationChannel Channel { get; set; }

    /// <summary>Whether the channel is enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Preferred locale (e.g. "th-TH"), or null for the app default.</summary>
    public string? Locale { get; set; }
}
