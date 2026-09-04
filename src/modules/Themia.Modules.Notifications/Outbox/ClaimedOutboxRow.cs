using Themia.Messaging.Outbox;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>A row claimed from the outbox for delivery: the columns the drainer needs to send the
/// message and then mark it complete or failed.</summary>
/// <param name="Id">The outbox row primary key.</param>
/// <param name="TenantId">The owning tenant, or <see langword="null"/> for a host-level message.</param>
/// <param name="Channel">The delivery channel.</param>
/// <param name="Recipient">The channel-specific recipient address.</param>
/// <param name="Subject">The message subject, where the channel uses one.</param>
/// <param name="Body">The rendered message body.</param>
/// <param name="Attempts">The number of delivery attempts already made before this claim.</param>
/// <param name="DeliveryOptions">
/// The row's <c>delivery_options</c> JSON (cc, bcc, text/plain alternative, headers), or
/// <see langword="null"/>. Opaque here on purpose — <see cref="NotificationDeliveryOptions"/> is the only
/// place the shape is known, so the dialects carry a string and cannot disagree about it.
/// </param>
public sealed record ClaimedOutboxRow(
    Guid Id,
    string? TenantId,
    NotificationChannel Channel,
    string Recipient,
    string? Subject,
    string Body,
    int Attempts,
    string? DeliveryOptions = null) : IClaimedRow;
