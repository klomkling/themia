namespace Themia.Messaging.Outbox;

/// <summary>A generic message row claimed for delivery: what the transport needs to send it and what the
/// drainer needs to mark it complete or failed.</summary>
/// <param name="Id">The outbox row primary key.</param>
/// <param name="MessageId">The stable message identifier the receiver deduplicates on.</param>
/// <param name="TenantId">The owning tenant, or <see langword="null"/> for a host-level message.</param>
/// <param name="Type">The logical message type the receiver routes on.</param>
/// <param name="Payload">The serialized body, carried verbatim.</param>
/// <param name="Destination">The logical peer this is addressed to.</param>
/// <param name="Origin">The system that originated the message (not the last hop).</param>
/// <param name="EntityKey">The key a staleness fence applies within, or <see langword="null"/>.</param>
/// <param name="Version">The monotonic version for <paramref name="EntityKey"/>, or <see langword="null"/>.</param>
/// <param name="Attempts">The number of delivery attempts already made before this claim.</param>
public sealed record ClaimedMessageRow(
    Guid Id,
    Guid MessageId,
    string? TenantId,
    string Type,
    string Payload,
    string Destination,
    string Origin,
    string? EntityKey,
    long? Version,
    int Attempts) : IClaimedRow;
