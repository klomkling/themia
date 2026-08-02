using System.Text.Json;

using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Messaging;
using Themia.Messaging.Messages;
using Themia.Messaging.Outbox;
using Themia.Modules.Messaging.Entities;

namespace Themia.Modules.Messaging.Stores;

/// <summary>Repository-backed <see cref="IMessageOutboxStore"/>. Peer-agnostic: the framework binds the
/// injected repository to EF or Dapper. The repository stamps the tenant on insert; the caller's unit of
/// work commits, so a published message can never survive a rolled-back transaction.</summary>
internal sealed class MessageOutboxStore(
    IRepository<MessageOutboxEntry, Guid> repository,
    TimeProvider time,
    MessagingIdentity identity) : IMessageOutboxStore
{
    /// <inheritdoc />
    public Task EnqueueAsync(MessageEnvelope message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Validate here so a malformed message fails at the call site rather than hours later in the drainer.
        message.Validate();

        var now = time.GetUtcNow();
        var entry = new MessageOutboxEntry
        {
            // TenantId is deliberately left unset here: MessageEnvelope carries no tenant field (a
            // caller-supplied tenant on an INSERT bypasses ThemiaDbContext.ValidateTenantWritesAsync, which
            // only validates Modified/Deleted entries — an explicit value here would let a request
            // authenticated as tenant A stamp a row as tenant B). The repository stamps the ambient tenant
            // on insert whenever the entity's TenantId is still null, which is the only source of truth.
            MessageId = message.MessageId,
            Type = message.Type,
            Payload = message.Payload,
            Destination = message.Destination,
            // The envelope's Origin wins when set; otherwise fall back to this service's identity.
            // MessagingIdentity's constructor already guarantees Origin is non-blank.
            Origin = string.IsNullOrWhiteSpace(message.Origin) ? identity.Origin : message.Origin,
            EntityKey = message.EntityKey,
            Version = message.Version,
            Headers = message.Headers is null ? null : JsonSerializer.Serialize(message.Headers),
            Status = OutboxStatus.Pending,
            Attempts = 0,
            ScheduledFor = message.ScheduledFor,
            NextAttemptAt = message.ScheduledFor ?? now,
            CreatedAt = message.CreatedAt == default ? now : message.CreatedAt,
        };
        entry.SetId(Guid.CreateVersion7());

        return repository.AddAsync(entry, ct);
    }
}
