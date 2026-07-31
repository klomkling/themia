using System.Text.Json;

using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Messaging.Messages;
using Themia.Messaging.Outbox;
using Themia.Modules.Messaging.Entities;

namespace Themia.Modules.Messaging.Stores;

/// <summary>Repository-backed <see cref="IMessageOutboxStore"/>. Peer-agnostic: the framework binds the
/// injected repository to EF or Dapper. The repository stamps the tenant on insert; the caller's unit of
/// work commits, so a published message can never survive a rolled-back transaction.</summary>
internal sealed class MessageOutboxStore(
    IRepository<MessageOutboxEntry, Guid> repository,
    TimeProvider time) : IMessageOutboxStore
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
            MessageId = message.MessageId,
            Type = message.Type,
            Payload = message.Payload,
            Destination = message.Destination,
            Origin = message.Origin,
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
