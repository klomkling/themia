using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Entities;

/// <summary>
/// A queued, self-contained notification awaiting delivery by the background drainer.
/// Bodies are rendered at enqueue time, so the drainer never touches templates.
/// </summary>
public sealed class OutboxMessage : Entity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The delivery channel (Email / Sms / Push). In-app is written directly, never via the outbox.</summary>
    public NotificationChannel Channel { get; set; }

    /// <summary>Email address / phone number / push token.</summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>Optional subject (email).</summary>
    public string? Subject { get; set; }

    /// <summary>The final, already-rendered body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Lifecycle state.</summary>
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    /// <summary>Number of delivery attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time the message may be (re)attempted.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>If set, the message is held until this time (future-dated sends).</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Identifier of the drainer instance currently holding the row.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>When the current lease expires; a past value on a <c>Sending</c> row is reclaimable.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>When the row was created/enqueued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the message was successfully sent.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>The last failure message, if any (never contains credentials/PII).</summary>
    public string? LastError { get; set; }
}
