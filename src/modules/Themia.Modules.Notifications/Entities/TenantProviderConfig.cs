using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Entities;

/// <summary>
/// Per-tenant provider credentials for a channel, resolved at send time with a
/// global fallback. v1 stores secrets as plain columns; encryption-at-rest is a follow-on.
/// </summary>
public sealed class TenantProviderConfig : AuditableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The channel these credentials apply to (Email / Sms).</summary>
    public NotificationChannel Channel { get; set; }

    /// <summary>SMTP host (email) — null for non-email channels.</summary>
    public string? Host { get; set; }

    /// <summary>SMTP port (email).</summary>
    public int? Port { get; set; }

    /// <summary>Provider username / API key id.</summary>
    public string? Username { get; set; }

    /// <summary>Provider password / API secret. // ponytail: plain column in v1; Data Protection later.</summary>
    public string? Password { get; set; }

    /// <summary>From-address (email) or sender id (SMS).</summary>
    public string? FromAddress { get; set; }

    /// <summary>Whether SMTP uses SSL/STARTTLS.</summary>
    public bool UseSsl { get; set; } = true;
}
