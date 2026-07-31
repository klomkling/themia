using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Messaging.Entities;

/// <summary>
/// A message staged for delivery to another service. The persisted form of
/// <see cref="Themia.Messaging.Messages.MessageEnvelope"/>; <see cref="Payload"/> stays opaque and is never
/// deserialized by the framework.
/// </summary>
public sealed class MessageOutboxEntry : Entity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>Stable identifier the receiver deduplicates on; never reassigned across retries.</summary>
    public Guid MessageId { get; set; }

    /// <summary>The logical message type the receiver routes on.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The serialized body, carried verbatim.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>The logical peer this is addressed to.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>The system that originated the message — not the last hop.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>The key the receiver's own staleness fence applies within, if any.</summary>
    public string? EntityKey { get; set; }

    /// <summary>A monotonic version for <see cref="EntityKey"/>, carried for the receiver's fence.</summary>
    public long? Version { get; set; }

    /// <summary>Extra transport metadata as JSON. Never contains credentials.</summary>
    public string? Headers { get; set; }

    /// <summary>Lifecycle state.</summary>
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    /// <summary>Number of delivery attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time the message may be (re)attempted.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>If set, the message is held until this time.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Identifier of the drainer instance currently holding the row.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>When the current lease expires; a past value on a sending row is reclaimable.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>When the row was enqueued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the message was successfully delivered.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>The last failure message, if any. Never contains credentials.</summary>
    public string? LastError { get; set; }

    /// <summary>Assigns the identifier for a new (transient) row.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
