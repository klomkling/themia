using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>Repository-backed <see cref="IOutboxStore"/>. Peer-agnostic: the framework binds the injected
/// repository to EF or Dapper. The repository stamps the tenant on insert; the caller's unit of work commits.</summary>
internal sealed class OutboxStore(IRepository<OutboxMessage, Guid> repository) : IOutboxStore
{
    // Stages the insert (repository stamps TenantId); the caller's IUnitOfWork.SaveChangesAsync commits it (rollback-safe).
    public Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return repository.AddAsync(message, ct);
    }
}
