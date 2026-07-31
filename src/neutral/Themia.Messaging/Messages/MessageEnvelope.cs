namespace Themia.Messaging.Messages;

/// <summary>
/// A message staged for delivery to another service. The framework treats <see cref="Payload"/> as
/// opaque — it never deserializes it and has no knowledge of what a given <see cref="Type"/> means, so
/// domain event contracts stay in the application that owns them.
/// </summary>
public sealed class MessageEnvelope
{
    /// <summary>
    /// Stable identifier for this message, generated once at enqueue and never reassigned on retry.
    /// This is the key the receiver deduplicates on, so a redelivery must carry the SAME id — a new id
    /// per attempt would defeat the inbox entirely.
    /// </summary>
    /// <remarks>
    /// MUST be globally unique across every tenant and every peer — generate it with
    /// <see cref="Guid.CreateVersion7()"/> (or an equivalent random GUID), never derive it deterministically
    /// from tenant-scoped data (e.g. a per-tenant sequence). The receiving inbox dedups on
    /// <c>(origin, message_id)</c> with no tenant component in the key, so a deterministically-derived id
    /// that collides across two tenants is silently treated as a redelivery of the first tenant's message
    /// and dropped for the second — the same permanent message loss the inbox exists to prevent, just
    /// crossing a tenant boundary instead of a crash window.
    /// </remarks>
    public Guid MessageId { get; set; }

    /// <summary>The owning tenant, or <see langword="null"/> for a single-org or host-level message.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The logical message type the receiver routes on, e.g. <c>listing.snapshot.v1</c>. Versioning the
    /// type is the adopter's business; the framework only carries the string.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The serialized message body, carried verbatim. Never inspected by the framework.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>The logical peer this is addressed to; configuration maps the name to an endpoint.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// The system that ORIGINATED the message — not the last hop. Preserved across forwarding so a
    /// bi-directional topology can drop anything that arrives back at its own origin. Optional: when left
    /// unset, the module's configured <c>MessagingModuleOptions.Origin</c> is used instead.
    /// </summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>
    /// The key the receiver's staleness fence compares versions within, e.g. the listing id. Optional:
    /// events that are not state snapshots have nothing meaningful to fence on.
    /// </summary>
    public string? EntityKey { get; set; }

    /// <summary>
    /// A monotonic version for <see cref="EntityKey"/>, carried so a receiver can reject a snapshot older
    /// than the state it already holds. Optional, and meaningless without <see cref="EntityKey"/>.
    /// </summary>
    public long? Version { get; set; }

    /// <summary>Extra transport metadata sent alongside the payload. Never contains credentials.</summary>
    public IDictionary<string, string>? Headers { get; set; }

    /// <summary>When the message was enqueued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>If set, the message is held until this time rather than sent on the next drain.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Validates the envelope, throwing if a required field is missing or inconsistent.</summary>
    /// <exception cref="ArgumentException">A required field is empty, or a version has no entity key.</exception>
    public void Validate()
    {
        if (MessageId == Guid.Empty)
            throw new ArgumentException("Must not be empty.", nameof(MessageId));
        if (string.IsNullOrWhiteSpace(Type))
            throw new ArgumentException("Must not be null or whitespace.", nameof(Type));
        if (string.IsNullOrWhiteSpace(Destination))
            throw new ArgumentException("Must not be null or whitespace.", nameof(Destination));

        // Origin is optional here: an unset value falls back to the module's configured Origin at
        // enqueue time (see MessagingModuleOptions.Origin), which Validate() has no access to.

        // A version with nothing to scope it to cannot be compared against anything, so it would fence
        // nothing while looking like it does — reject it rather than silently ignore it.
        if (Version is not null && string.IsNullOrWhiteSpace(EntityKey))
            throw new ArgumentException($"{nameof(Version)} requires {nameof(EntityKey)}.", nameof(EntityKey));
    }
}
